using Cratebase.Core;

namespace Cratebase.Admin;

/// <summary>
/// Log settings.
/// </summary>
public sealed record LogSettings
{
    /// <summary>Are API requests logged?</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Retention period, in days. Zero keeps entries without limit.
    /// </summary>
    /// <remarks>
    /// Seven days by default: enough to investigate a weekend incident, little enough that a
    /// forgotten instance doesn't fill its disk with traces nobody will read.
    /// </remarks>
    public int RetentionDays { get; init; } = 7;

    /// <summary>Minimum severity actually written.</summary>
    public LogSeverity MinLevel { get; init; } = LogSeverity.Info;

    /// <summary>
    /// Is the origin address retained?
    /// </summary>
    /// <remarks>
    /// Configurable, not merely decorative: an IP address is personal data under GDPR. An instance
    /// that has no use for it must be able to stop collecting it without giving up the log
    /// altogether.
    /// </remarks>
    public bool LogIp { get; init; } = true;
}

/// <summary>
/// Instance settings, editable from the console.
/// </summary>
/// <remarks>
/// <para>
/// Whatever lands here must be <b>honored by the engine</b>. A settings screen that shows toggles
/// with no effect is worse than no screen at all: it fakes a control that doesn't exist. Secrets —
/// OAuth2 client secrets, connection string — stay in host configuration and never appear here: a
/// table the console can read ends up in a backup.
/// </para>
/// </remarks>
/// <summary>Realtime settings.</summary>
public sealed record RealtimeSettings
{
    /// <summary>Number of simultaneous streams beyond which a new connection is refused.</summary>
    public const int MaxClientsCeiling = 10_000;

    /// <summary>Is realtime open?</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Simultaneous streams admitted.
    /// </summary>
    /// <remarks>
    /// A hard limit, not a convenience setting: every stream holds a connection and a message queue
    /// for its whole duration. Without a ceiling, a client that keeps reopening its stream exhausts
    /// the server's connections without ever asking for anything illegitimate.
    /// </remarks>
    public int MaxClients { get; init; } = 200;
}

public sealed record AppSettings
{
    /// <summary>Instance name, shown by the console.</summary>
    public string AppName { get; init; } = "Cratebase";

    /// <summary>Public URL of the instance. Used to compose OAuth2 redirect URLs.</summary>
    public string AppUrl { get; init; } = string.Empty;

    /// <summary>Log settings.</summary>
    public LogSettings Logs { get; init; } = new();

    /// <summary>Realtime settings.</summary>
    public RealtimeSettings Realtime { get; init; } = new();

    /// <summary>
    /// Validates and normalizes the submitted settings.
    /// </summary>
    /// <exception cref="CratebaseValidationException">A setting is out of bounds.</exception>
    public AppSettings Validated()
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = AppName.Trim();
        var url = AppUrl.Trim().TrimEnd('/');

        if (name.Length == 0)
        {
            errors["appName"] = ["The instance name is required."];
        }
        else if (name.Length > 100)
        {
            errors["appName"] = ["The name cannot exceed 100 characters."];
        }

        if (url.Length > 0 &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
             parsed.Scheme is not ("http" or "https")))
        {
            errors["appUrl"] = ["The URL must be absolute and use http or https, e.g. https://example.org."];
        }

        if (Logs.RetentionDays is < 0 or > 365)
        {
            errors["logs.retentionDays"] = ["Retention ranges from 0 (unlimited) to 365 days."];
        }

        if (Realtime.MaxClients is < 1 || Realtime.MaxClients > RealtimeSettings.MaxClientsCeiling)
        {
            errors["realtime.maxClients"] =
                [$"The number of streams ranges from 1 to {RealtimeSettings.MaxClientsCeiling}."];
        }

        if (errors.Count > 0)
        {
            throw new CratebaseValidationException(errors);
        }

        return this with { AppName = name, AppUrl = url };
    }
}
