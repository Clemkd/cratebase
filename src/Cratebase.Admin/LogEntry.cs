using Cratebase.Core;

namespace Cratebase.Admin;

/// <summary>
/// Severity of a log entry.
/// </summary>
/// <remarks>
/// Four levels, like PocketBase, no more: beyond that, nobody knows which level to pick when
/// writing, and the log screen's filter stops meaning anything. Declaration order <b>is</b>
/// severity order — <see cref="LogSeverities.AtLeast"/> uses it to translate "at least Warning"
/// into a list of values.
/// </remarks>
public enum LogSeverity
{
    /// <summary>Debugging detail.</summary>
    Debug,

    /// <summary>Normal flow: a served request.</summary>
    Info,

    /// <summary>Anomaly attributable to the caller: 4xx, access refusal.</summary>
    Warning,

    /// <summary>Anomaly attributable to the server: 5xx, untranslated exception.</summary>
    Error,
}

/// <summary>Operations on severity levels.</summary>
public static class LogSeverities
{
    /// <summary>All levels, from least to most severe.</summary>
    public static IReadOnlyList<LogSeverity> All { get; } =
        [LogSeverity.Debug, LogSeverity.Info, LogSeverity.Warning, LogSeverity.Error];

    /// <summary>Levels at least as severe as the requested one.</summary>
    public static IReadOnlyList<LogSeverity> AtLeast(LogSeverity minimum) =>
        [.. All.Where(level => level >= minimum)];

    /// <summary>Level corresponding to an HTTP status.</summary>
    /// <remarks>
    /// A 4xx is a warning, not an error: it's the normal operation of a public API — a badly
    /// written filter, an expired token. Ranking both at the same level would drown real outages in
    /// client noise.
    /// </remarks>
    public static LogSeverity ForStatus(int status) => status switch
    {
        >= 500 => LogSeverity.Error,
        >= 400 => LogSeverity.Warning,
        _ => LogSeverity.Info,
    };
}

/// <summary>
/// Log entry.
/// </summary>
/// <remarks>
/// <para>
/// A request's attributes have their own column instead of living in <see cref="Data"/>: filtering
/// by status or method is this screen's common gesture, and doing it through JSON would require an
/// engine-specific extraction — exactly what rule R1 forbids outside the <c>Cratebase.Data.*</c>
/// packages.
/// </para>
/// <para>
/// No property is null: an application entry with no HTTP request simply carries empty strings and
/// zeros. <c>NULL</c> would propagate into filters — <c>status &lt;&gt; 200</c> would stop matching
/// rows with no status — to express nothing more.
/// </para>
/// </remarks>
public sealed record LogEntry
{
    /// <summary>Identifier. UUIDv7: already sorts in creation order.</summary>
    public RecordId Id { get; init; } = RecordId.New();

    /// <summary>Write instant.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>Severity.</summary>
    public LogSeverity Level { get; init; } = LogSeverity.Info;

    /// <summary>Human-readable message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>HTTP method, for a request entry.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>Called path, including the query string.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Returned status. Zero outside a request.</summary>
    public int Status { get; init; }

    /// <summary>Processing time, in milliseconds.</summary>
    public double Duration { get; init; }

    /// <summary>Caller's auth collection.</summary>
    public string AuthCollection { get; init; } = string.Empty;

    /// <summary>Caller's identifier.</summary>
    public string AuthId { get; init; } = string.Empty;

    /// <summary>Origin address, if its collection is enabled.</summary>
    public string Ip { get; init; } = string.Empty;

    /// <summary>User agent.</summary>
    public string UserAgent { get; init; } = string.Empty;

    /// <summary>Referer.</summary>
    public string Referer { get; init; } = string.Empty;

    /// <summary>Free-form details: error message, submitted filter, retained headers.</summary>
    public IReadOnlyDictionary<string, object?> Data { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>Granularity of the log histogram.</summary>
/// <remarks>
/// Only three tiers, and each corresponds to a prefix length of the canonical form — that's what
/// makes grouping identical across both engines, with no date function.
/// </remarks>
public enum LogGranularity
{
    /// <summary>One point per minute. For a one-hour window.</summary>
    Minute,

    /// <summary>One point per hour.</summary>
    Hour,

    /// <summary>One point per day.</summary>
    Day,
}

/// <summary>A point of the histogram.</summary>
/// <param name="Bucket">Start of the slice, in canonical form.</param>
/// <param name="Level">Counted severity.</param>
/// <param name="Count">Number of entries.</param>
public sealed record LogBucket(string Bucket, LogSeverity Level, long Count);
