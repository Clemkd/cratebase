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
/// Identity of the running instance.
/// </summary>
/// <remarks>
/// Instantiated once, at startup: uptime is counted from Cratebase's initialization, not from the
/// first call to the administration screen.
/// </remarks>
public sealed class InstanceDescriptor(IClock clock)
{
    /// <summary>Initialization instant.</summary>
    public DateTimeOffset StartedAt { get; } = (clock ?? throw new ArgumentNullException(nameof(clock))).UtcNow;

    /// <summary>Product version.</summary>
    public string Version { get; } =
        typeof(InstanceDescriptor).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "unknown";

    /// <summary>Runtime version.</summary>
    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;
}

/// <summary>
/// Settings submitted by the console.
/// </summary>
/// <remarks>
/// Every property is optional, and an absent property leaves the value in place. Accepting
/// <see cref="AppSettings"/> directly would have turned a partial PATCH into a full replacement: a
/// client that only sends the name would reset retention and address collection to their defaults,
/// with no warning at all.
/// </remarks>
public sealed record SettingsRequest
{
    /// <summary>Instance name.</summary>
    public string? AppName { get; init; }

    /// <summary>Public URL.</summary>
    public string? AppUrl { get; init; }

    /// <summary>Log settings.</summary>
    public LogSettingsRequest? Logs { get; init; }

    /// <summary>Realtime settings.</summary>
    public RealtimeSettingsRequest? Realtime { get; init; }

    /// <summary>Applies the supplied values to the current settings.</summary>
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

/// <summary>Realtime settings submitted by the console.</summary>
public sealed record RealtimeSettingsRequest
{
    /// <summary>Is realtime open?</summary>
    public bool? Enabled { get; init; }

    /// <summary>Simultaneous streams admitted.</summary>
    public int? MaxClients { get; init; }

    /// <summary>Applies the supplied values to the current settings.</summary>
    public RealtimeSettings Apply(RealtimeSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return new RealtimeSettings
        {
            // `?? current`, not `?? default`: that's what distinguishes "absent" from "false".
            Enabled = Enabled ?? current.Enabled,
            MaxClients = MaxClients ?? current.MaxClients,
        };
    }
}

/// <summary>Log settings submitted by the console.</summary>
public sealed record LogSettingsRequest
{
    /// <summary>Request logging.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Retention, in days.</summary>
    public int? RetentionDays { get; init; }

    /// <summary>Minimum severity written.</summary>
    public LogSeverity? MinLevel { get; init; }

    /// <summary>Collection of the origin address.</summary>
    public bool? LogIp { get; init; }

    /// <summary>Applies the supplied values.</summary>
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
/// Operations endpoints: settings and instance status.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>Publishes <c>/settings</c> and <c>/instance</c>.</summary>
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

            // The author is carried by the authentication columns, not by the free-form details:
            // that's what the log screen's "Author" column reads, and a settings change attributed
            // to "anonymous" would be misleading.
            logs.Record(new LogEntry
            {
                Level = LogSeverity.Warning,
                Message = "Instance settings changed.",
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

                    // Buffer drops are shown rather than hidden: an incomplete log that says so
                    // stays usable, an incomplete log that stays silent proves nothing.
                    dropped = logs.Dropped,
                    settings.Current.Logs.Enabled,
                    settings.Current.Logs.RetentionDays,
                },

                // ⚠️ No client secret, and nothing derived from one: this payload says a provider is
                // configured, never with what.
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

                    // SQLite puts its file on the host's disk: its gauge is the disk's, and no
                    // second one is needed. A PostgreSQL server, on the other hand, lives
                    // elsewhere — often on another machine.
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
    /// Capacity of the volume that carries a directory.
    /// </summary>
    /// <remarks>
    /// The volume is inferred from the path rather than hardcoded: in a container, the data
    /// directory is almost always a mount distinct from the root, and measuring the root would
    /// report the space of a disk the data doesn't occupy.
    ///
    /// A failure is a response like any other — a network path, an exotic file system, a missing
    /// permission. It's returned as-is rather than disguised as zero: a gauge at zero reads as a
    /// full disk.
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
    /// Database size, measured by the engine itself.
    /// </summary>
    /// <remarks>
    /// The query comes from the dialect: it's the only way to return the same figure on both
    /// engines without this file naming either. A failure returns zero rather than bringing down
    /// the whole screen — volumetrics is a convenience, not a reason to lose administration.
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
