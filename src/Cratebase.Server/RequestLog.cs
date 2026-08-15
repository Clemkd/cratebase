using System.Diagnostics;
using Cratebase.Admin;
using Cratebase.Auth;
using Cratebase.Core;
using Microsoft.AspNetCore.Http;

namespace Cratebase.Server;

/// <summary>
/// Journalise les requêtes servies par l'API.
/// </summary>
/// <remarks>
/// <para>
/// Posé <b>autour</b> des endpoints et non dedans : une requête refusée par une règle d'accès, ou
/// interrompue par une exception, doit laisser une trace autant qu'une requête servie — c'est même
/// la seule qui intéresse un administrateur. D'où la capture de l'exception, sa journalisation,
/// puis sa relance intacte vers le gestionnaire qui la traduira.
/// </para>
/// <para>
/// Seules les requêtes de l'API sont retenues. Les fichiers statiques de la console — une centaine
/// par chargement — ne disent rien du fonctionnement du moteur et noieraient le journal.
/// </para>
/// </remarks>
public sealed class CratebaseRequestLogMiddleware(
    RequestDelegate next,
    LogStore logs,
    SettingsStore settings,
    CratebaseOptions options)
{
    /// <summary>Longueur maximale conservée pour une URL ou un en-tête.</summary>
    private const int MaxTextLength = 1024;

    /// <summary>
    /// Paramètres dont la valeur est masquée dans le journal.
    /// </summary>
    /// <remarks>
    /// Le jeton de fichier circule dans l'URL — c'est assumé, sa durée de vie est de deux minutes.
    /// L'écrire tel quel dans une table que la console affiche prolongerait sa fenêtre
    /// d'exploitation de toute la durée de rétention du journal.
    /// </remarks>
    private static readonly string[] SensitiveParameters =
        ["token", "password", "secret", "code", "codeVerifier", "identity"];

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    private readonly LogStore _logs = logs ?? throw new ArgumentNullException(nameof(logs));

    private readonly SettingsStore _settings = settings
        ?? throw new ArgumentNullException(nameof(settings));

    private readonly CratebaseOptions _options = options
        ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Traite la requête.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Concerns(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var started = Stopwatch.GetTimestamp();

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            Write(context, Stopwatch.GetElapsedTime(started), failure);
            throw;
        }

        Write(context, Stopwatch.GetElapsedTime(started), null);
    }

    private bool Concerns(HttpContext context)
    {
        if (!_settings.Current.Logs.Enabled)
        {
            return false;
        }

        var path = context.Request.Path;

        if (!path.StartsWithSegments(_options.ApiPrefix))
        {
            return false;
        }

        // Consulter le journal ne doit pas le remplir : sans cette exception, chaque
        // rafraîchissement de l'écran ajouterait une ligne en tête de la page qu'on est en train
        // de lire, et la pagination glisserait sous les yeux de l'administrateur.
        return !(HttpMethods.IsGet(context.Request.Method) &&
                 path.StartsWithSegments($"{_options.ApiPrefix}/logs"));
    }

    private void Write(HttpContext context, TimeSpan elapsed, Exception? failure)
    {
        var status = failure switch
        {
            CratebaseException business => business.StatusCode,
            not null => StatusCodes.Status500InternalServerError,
            _ => context.Response.StatusCode,
        };

        var level = LogSeverities.ForStatus(status);
        var logging = _settings.Current.Logs;

        if (level < logging.MinLevel)
        {
            return;
        }

        var url = Describe(context.Request);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (failure is not null)
        {
            data["exception"] = failure.GetType().Name;
            data["detail"] = Truncate(failure.Message);
        }

        var caller = context.Items.TryGetValue(HttpCurrentUser.ContextKey, out var value)
            ? value as AuthenticatedRecord
            : null;

        if (caller?.IsSuperuser == true)
        {
            data["superadmin"] = true;
        }

        _logs.Record(new LogEntry
        {
            Level = level,
            Message = $"{context.Request.Method} {url} → {status}",
            Method = context.Request.Method,
            Url = url,
            Status = status,
            Duration = Math.Round(elapsed.TotalMilliseconds, 3),
            AuthCollection = caller?.Collection ?? string.Empty,
            AuthId = caller?.Id.ToString() ?? string.Empty,

            // L'adresse d'origine est celle de la connexion, jamais celle annoncée par un en-tête :
            // « X-Forwarded-For » est déclaratif, donc falsifiable. Derrière un répartiteur, c'est
            // à l'hôte d'installer UseForwardedHeaders, qui réécrit l'adresse de connexion après
            // avoir vérifié le mandataire.
            Ip = logging.LogIp ? context.Connection.RemoteIpAddress?.ToString() ?? string.Empty : string.Empty,
            UserAgent = Truncate(context.Request.Headers.UserAgent.ToString()),
            Referer = Truncate(context.Request.Headers.Referer.ToString()),
            Data = data,
        });
    }

    private static string Describe(HttpRequest request)
    {
        var path = request.Path.ToString();

        if (!request.QueryString.HasValue)
        {
            return Truncate(path);
        }

        var parameters = request.Query.Select(parameter =>
        {
            var masked = SensitiveParameters.Contains(parameter.Key, StringComparer.OrdinalIgnoreCase);

            return $"{parameter.Key}={(masked ? "***" : parameter.Value.ToString())}";
        });

        return Truncate($"{path}?{string.Join('&', parameters)}");
    }

    private static string Truncate(string? value) => value switch
    {
        null => string.Empty,
        { Length: <= MaxTextLength } => value,
        _ => value[..MaxTextLength] + "…",
    };
}
