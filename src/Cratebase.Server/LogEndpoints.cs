using System.Globalization;
using Cratebase.Admin;
using Cratebase.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cratebase.Server;

/// <summary>
/// Endpoints de consultation du journal.
/// </summary>
/// <remarks>
/// Réservés au super-admin : le journal porte les chemins appelés, les identifiants des
/// appelants et, selon les réglages, leur adresse. C'est la table la plus sensible de l'instance
/// après celle des comptes.
/// </remarks>
public static class LogEndpoints
{
    /// <summary>Fenêtre par défaut de l'histogramme, en heures.</summary>
    private const int DefaultStatsWindowHours = 24;

    /// <summary>Publie <c>/logs</c>.</summary>
    public static IEndpointRouteBuilder MapLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/logs");

        group.MapGet("/", async (
            HttpContext http,
            LogStore logs,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            CollectionEndpoints.RequireSuperuser(user);

            // Le tampon est vidé avant la lecture : sans cela, l'erreur qu'on vient de provoquer
            // n'apparaîtrait qu'au prochain passage du service de fond, et l'écran donnerait
            // l'impression de l'avoir perdue.
            await logs.FlushAsync(cancellationToken).ConfigureAwait(false);

            var query = ReadQuery(http);
            var page = await logs.QueryAsync(query, cancellationToken).ConfigureAwait(false);

            if (http.Request.Query["stats"] != "1")
            {
                return Results.Ok(page);
            }

            // ⚠️ L'histogramme est calculé ici, dans la même requête que la page, et non par un
            // second appel : deux appels vident le tampon chacun de leur côté, donc le second voit
            // des entrées que le premier n'avait pas. Le graphique annonçait alors dix-neuf entrées
            // au-dessus d'un tableau qui en comptait dix-sept — un écran qui se contredit lui-même.
            var granularity = ReadGranularity(http, query);
            var buckets = await logs.StatsAsync(query, granularity, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                page.Page,
                page.PerPage,
                page.TotalItems,
                page.TotalPages,
                page.Items,
                stats = Describe(query, granularity, buckets),
            });
        });

        group.MapGet("/stats", async (
            HttpContext http,
            LogStore logs,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            CollectionEndpoints.RequireSuperuser(user);

            await logs.FlushAsync(cancellationToken).ConfigureAwait(false);

            var query = ReadQuery(http);
            var granularity = ReadGranularity(http, query);

            var buckets = await logs.StatsAsync(query, granularity, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(Describe(query, granularity, buckets));
        });

        group.MapGet("/{id}", async (
            string id,
            LogStore logs,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            CollectionEndpoints.RequireSuperuser(user);

            if (!RecordId.TryParse(id, out var recordId))
            {
                throw new CratebaseNotFoundException("Entrée de journal introuvable.");
            }

            await logs.FlushAsync(cancellationToken).ConfigureAwait(false);

            var entry = await logs.GetAsync(recordId, cancellationToken).ConfigureAwait(false)
                ?? throw new CratebaseNotFoundException("Entrée de journal introuvable.");

            return Results.Ok(entry);
        });

        group.MapDelete("/", async (
            LogStore logs,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            CollectionEndpoints.RequireSuperuser(user);

            var deleted = await logs.ClearAsync(cancellationToken).ConfigureAwait(false);

            // La purge se journalise elle-même : un journal qui peut être vidé sans laisser de
            // trace de son vidage ne prouve plus rien.
            logs.Record(LogSeverity.Warning, $"Journal vidé : {deleted} entrées supprimées.");

            return Results.Ok(new { deleted });
        });

        return endpoints;
    }

    /// <summary>Met l'histogramme en forme pour le client.</summary>
    private static object Describe(
        LogQuery query,
        LogGranularity granularity,
        IReadOnlyList<LogBucket> buckets) => new
    {
        granularity,
        from = query.From,
        to = query.To,
        items = buckets.Select(bucket => new
        {
            // La tranche est rendue en instant complet : le client n'a pas à savoir que le
            // regroupement se fait sur un préfixe de texte.
            bucket = Timestamp.Normalize(LogBuckets.ToInstant(bucket.Bucket)),
            level = bucket.Level,
            count = bucket.Count,
        }),
    };

    private static LogQuery ReadQuery(HttpContext http) => new()
    {
        Page = ReadInt(http, "page") ?? 1,
        PerPage = ReadInt(http, "perPage") ?? 50,
        Levels = ReadLevels(http),
        Search = http.Request.Query["q"],
        Method = http.Request.Query["method"],
        Status = ReadInt(http, "status"),
        From = ReadInstant(http, "from"),
        To = ReadInstant(http, "to"),
        Ascending = http.Request.Query["sort"] == "created",
    };

    /// <summary>
    /// Granularité de l'histogramme.
    /// </summary>
    /// <remarks>
    /// Déduite de la fenêtre quand le client ne la précise pas : au-delà de deux jours, un point
    /// par heure produit des centaines de barres illisibles ; en deçà, un point par jour en produit
    /// une seule.
    /// </remarks>
    private static LogGranularity ReadGranularity(HttpContext http, LogQuery query)
    {
        if (Enum.TryParse<LogGranularity>(http.Request.Query["granularity"], ignoreCase: true, out var requested))
        {
            return requested;
        }

        var from = query.From ?? DateTimeOffset.UtcNow.AddHours(-DefaultStatsWindowHours);
        var to = query.To ?? DateTimeOffset.UtcNow;
        var span = to - from;

        if (span > TimeSpan.FromDays(2)) return LogGranularity.Day;

        return span <= TimeSpan.FromHours(2) ? LogGranularity.Minute : LogGranularity.Hour;
    }

    /// <summary>
    /// Niveaux demandés : <c>?level=Warning,Error</c>, ou <c>?level=</c> répété.
    /// </summary>
    /// <remarks>
    /// Un nom inconnu est ignoré plutôt que rejeté. Le paramètre vient d'une barre d'adresse qu'on
    /// bricole, et rendre une erreur 400 sur une faute de frappe transformerait une consultation en
    /// panne apparente ; ne rien retenir de fautif suffit.
    /// </remarks>
    private static IReadOnlyList<LogSeverity> ReadLevels(HttpContext http) =>
    [
        .. http.Request.Query["level"]
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(name => Enum.TryParse<LogSeverity>(name, ignoreCase: true, out var level) ? level : (LogSeverity?)null)
            .Where(level => level is not null)
            .Select(level => level!.Value)
            .Distinct(),
    ];

    private static int? ReadInt(HttpContext http, string name) =>
        int.TryParse(http.Request.Query[name], CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DateTimeOffset? ReadInstant(HttpContext http, string name) =>
        DateTimeOffset.TryParse(
            http.Request.Query[name],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;
}
