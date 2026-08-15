using System.Reflection;
using System.Runtime.InteropServices;
using Cratebase.Admin;
using Cratebase.Core;
using Cratebase.Schema;
using Cratebase.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cratebase.Server;

/// <summary>
/// Identité de l'instance en cours d'exécution.
/// </summary>
/// <remarks>
/// Instancié une fois, au démarrage : la durée de fonctionnement se compte depuis l'initialisation
/// de Cratebase, pas depuis le premier appel à l'écran d'administration.
/// </remarks>
public sealed class InstanceDescriptor(IClock clock)
{
    /// <summary>Instant d'initialisation.</summary>
    public DateTimeOffset StartedAt { get; } = (clock ?? throw new ArgumentNullException(nameof(clock))).UtcNow;

    /// <summary>Version du produit.</summary>
    public string Version { get; } =
        typeof(InstanceDescriptor).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "inconnue";

    /// <summary>Version d'exécution.</summary>
    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;
}

/// <summary>
/// Réglages soumis par la console.
/// </summary>
/// <remarks>
/// Toutes les propriétés sont facultatives, et une propriété absente laisse la valeur en place.
/// Accepter directement <see cref="AppSettings"/> aurait fait d'un PATCH partiel un remplacement
/// intégral : un client qui n'envoie que le nom remettrait la rétention et la collecte d'adresse à
/// leurs valeurs par défaut, sans le moindre message.
/// </remarks>
public sealed record SettingsRequest
{
    /// <summary>Nom de l'instance.</summary>
    public string? AppName { get; init; }

    /// <summary>URL publique.</summary>
    public string? AppUrl { get; init; }

    /// <summary>Réglages du journal.</summary>
    public LogSettingsRequest? Logs { get; init; }

    /// <summary>Applique les valeurs fournies aux réglages en vigueur.</summary>
    public AppSettings Apply(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return new AppSettings
        {
            AppName = AppName ?? current.AppName,
            AppUrl = AppUrl ?? current.AppUrl,
            Logs = Logs?.Apply(current.Logs) ?? current.Logs,
        };
    }
}

/// <summary>Réglages du journal soumis par la console.</summary>
public sealed record LogSettingsRequest
{
    /// <summary>Journalisation des requêtes.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Rétention, en jours.</summary>
    public int? RetentionDays { get; init; }

    /// <summary>Gravité minimale écrite.</summary>
    public LogSeverity? MinLevel { get; init; }

    /// <summary>Collecte de l'adresse d'origine.</summary>
    public bool? LogIp { get; init; }

    /// <summary>Applique les valeurs fournies.</summary>
    public LogSettings Apply(LogSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return new LogSettings
        {
            Enabled = Enabled ?? current.Enabled,
            RetentionDays = RetentionDays ?? current.RetentionDays,
            MinLevel = MinLevel ?? current.MinLevel,
            LogIp = LogIp ?? current.LogIp,
        };
    }
}

/// <summary>
/// Endpoints d'exploitation : réglages et état de l'instance.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>Publie <c>/settings</c> et <c>/instance</c>.</summary>
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/settings", (SettingsStore settings, ICurrentUser user) =>
        {
            CollectionEndpoints.RequireSuperuser(user);

            return Results.Ok(settings.Current);
        });

        endpoints.MapPatch("/settings", async (
            SettingsRequest request,
            SettingsStore settings,
            LogStore logs,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            CollectionEndpoints.RequireSuperuser(user);
            ArgumentNullException.ThrowIfNull(request);

            var saved = await settings.SaveAsync(request.Apply(settings.Current), cancellationToken)
                .ConfigureAwait(false);

            // L'auteur est porté par les colonnes d'authentification, et non par les détails
            // libres : c'est ce que la colonne « Auteur » de l'écran des journaux lit, et une
            // modification de réglages attribuée à « anonyme » serait un contresens.
            logs.Record(new LogEntry
            {
                Level = LogSeverity.Warning,
                Message = "Réglages de l'instance modifiés.",
                AuthCollection = user.CollectionName ?? string.Empty,
                AuthId = user.Id?.ToString() ?? string.Empty,
            });

            return Results.Ok(saved);
        });

        endpoints.MapGet("/instance", async (
            CratebaseOptions options,
            CollectionRegistry registry,
            SettingsStore settings,
            LogStore logs,
            IObjectStore store,
            InstanceDescriptor instance,
            IClock clock,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            CollectionEndpoints.RequireSuperuser(user);

            var collections = registry.All();

            return Results.Ok(new
            {
                appName = settings.Current.AppName,
                appUrl = settings.Current.AppUrl,
                version = instance.Version,
                runtime = instance.Runtime,
                startedAt = instance.StartedAt,
                uptimeSeconds = (long)(clock.UtcNow - instance.StartedAt).TotalSeconds,
                engine = options.Dialect.Name,
                storage = store.Name,
                dataDirectory = Path.GetFullPath(options.DataDirectory),
                apiPrefix = options.ApiPrefix,
                collections = new
                {
                    total = collections.Count,
                    data = collections.Count(item => !item.IsSystem && item.Kind == CollectionKind.Base),
                    auth = collections.Count(item => item.Kind == CollectionKind.Auth),
                    view = collections.Count(item => item.Kind == CollectionKind.View),
                    system = collections.Count(item => item.IsSystem),
                },
                logs = new
                {
                    total = await logs.CountAsync(cancellationToken).ConfigureAwait(false),

                    // Les pertes du tampon sont affichées plutôt que tues : un journal incomplet
                    // qui le dit reste exploitable, un journal incomplet qui se tait ne prouve rien.
                    dropped = logs.Dropped,
                    settings.Current.Logs.Enabled,
                    settings.Current.Logs.RetentionDays,
                },

                // ⚠️ Ni secret client, ni rien qui en dérive : cette charge dit qu'un fournisseur
                // est configuré, jamais avec quoi.
                providers = options.OAuth2Providers.Values.Select(provider => new
                {
                    provider.Name,
                    provider.DisplayName,
                    provider.Enabled,
                    provider.AuthorizationUrl,
                    provider.Scopes,
                    provider.UsePkce,
                }),
            });
        });

        return endpoints;
    }
}
