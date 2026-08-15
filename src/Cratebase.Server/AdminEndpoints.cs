using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Cratebase.Admin;
using Cratebase.Core;
using Cratebase.Data;
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

    /// <summary>Réglages du temps réel.</summary>
    public RealtimeSettingsRequest? Realtime { get; init; }

    /// <summary>Applique les valeurs fournies aux réglages en vigueur.</summary>
    public AppSettings Apply(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return new AppSettings
        {
            AppName = AppName ?? current.AppName,
            AppUrl = AppUrl ?? current.AppUrl,
            Logs = Logs?.Apply(current.Logs) ?? current.Logs,
            Realtime = Realtime?.Apply(current.Realtime) ?? current.Realtime,
        };
    }
}

/// <summary>Réglages du temps réel soumis par la console.</summary>
public sealed record RealtimeSettingsRequest
{
    /// <summary>Le temps réel est-il ouvert ?</summary>
    public bool? Enabled { get; init; }

    /// <summary>Flux simultanés admis.</summary>
    public int? MaxClients { get; init; }

    /// <summary>Applique les valeurs fournies aux réglages en vigueur.</summary>
    public RealtimeSettings Apply(RealtimeSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return new RealtimeSettings
        {
            // `?? current` et non `?? défaut` : c'est ce qui distingue « absent » de « false ».
            Enabled = Enabled ?? current.Enabled,
            MaxClients = MaxClients ?? current.MaxClients,
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

        endpoints.MapGet("/usage", async (
            CratebaseOptions options,
            IDbConnectionFactory connections,
            IObjectStore store,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            CollectionEndpoints.RequireSuperuser(user);

            var description = options.StorageDescription;
            var onHostDisk = description.Kind == "local";

            long bytes = 0, objects = 0;

            await foreach (var info in store.ListInfoAsync(string.Empty, cancellationToken).ConfigureAwait(false))
            {
                if (info.Key.StartsWith(StorageEndpoints.DiagnosticsPrefix, StringComparison.Ordinal)) continue;

                objects++;
                bytes += info.Length;
            }

            var host = MeasureHost(onHostDisk ? description.Directory : options.DataDirectory);

            return Results.Ok(new
            {
                host,
                database = new
                {
                    engine = options.Dialect.Name,
                    bytes = await MeasureDatabaseAsync(connections, cancellationToken).ConfigureAwait(false),
                    capacityBytes = options.DatabaseCapacityBytes,

                    // SQLite pose son fichier sur le disque de l'hôte : sa jauge est celle du
                    // disque, et il n'en faut pas une seconde. Un serveur PostgreSQL, lui, vit
                    // ailleurs — souvent sur une autre machine.
                    onHostDisk = options.Dialect.Name == "sqlite",
                },
                files = new
                {
                    kind = description.Kind,
                    bytes,
                    objects,
                    capacityBytes = description.CapacityBytes,
                    onHostDisk,
                },
            });
        });

        return endpoints;
    }

    /// <summary>
    /// Capacité du volume qui porte un répertoire.
    /// </summary>
    /// <remarks>
    /// Le volume est déduit du chemin plutôt que codé en dur : sur un conteneur, le répertoire de
    /// données est presque toujours un montage distinct de la racine, et mesurer la racine
    /// annoncerait l'espace d'un disque que les données n'occupent pas.
    ///
    /// L'échec est une réponse comme une autre — un chemin réseau, un système de fichiers exotique,
    /// un droit manquant. Il est rendu tel quel, plutôt que déguisé en zéro : une jauge à zéro se
    /// lit comme un disque plein.
    /// </remarks>
    private static object MeasureHost(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            var root = Path.GetPathRoot(full);

            if (string.IsNullOrEmpty(root))
            {
                return new { available = false, path = full, totalBytes = 0L, freeBytes = 0L };
            }

            var drive = new DriveInfo(root);

            return new
            {
                available = drive.IsReady,
                path = drive.Name,
                totalBytes = drive.IsReady ? drive.TotalSize : 0L,
                freeBytes = drive.IsReady ? drive.AvailableFreeSpace : 0L,
            };
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new { available = false, path = directory, totalBytes = 0L, freeBytes = 0L };
        }
    }

    /// <summary>
    /// Taille de la base, mesurée par le moteur lui-même.
    /// </summary>
    /// <remarks>
    /// La requête vient du dialecte : c'est la seule façon de rendre le même chiffre sur les deux
    /// moteurs sans que ce fichier n'en nomme aucun. Un échec rend zéro plutôt que de faire tomber
    /// l'écran entier — la volumétrie est un confort, pas une raison de perdre l'administration.
    /// </remarks>
    private static async Task<long> MeasureDatabaseAsync(
        IDbConnectionFactory connections,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            command.CommandText = connections.Dialect.DatabaseSizeQuery;

            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (DbException)
        {
            return 0;
        }
    }
}
