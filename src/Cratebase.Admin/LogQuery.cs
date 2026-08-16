namespace Cratebase.Admin;

/// <summary>
/// Criteria for querying the log.
/// </summary>
/// <remarks>
/// A closed set of criteria, not the collections' filter language: the log isn't a collection — it
/// has neither a modifiable schema nor access rules — and opening the DSL to it would amount to
/// exposing a SQL compiler over a table nobody models. The five criteria below cover what one
/// actually looks for in a log: what, when, by whom, with what result.
/// </remarks>
public sealed record LogQuery
{
    /// <summary>Page size ceiling, aligned with the one for records.</summary>
    public const int MaxPerPage = 500;

    /// <summary>Requested page, starting at 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Page size.</summary>
    public int PerPage { get; init; } = 50;

    /// <summary>
    /// Retained levels. Empty or absent: all of them.
    /// </summary>
    /// <remarks>
    /// A set, not a minimum severity. "At least warning" is easily expressed with a set; the
    /// reverse isn't — isolating only warnings without errors is a common operations request, and a
    /// lower bound can't express it.
    /// </remarks>
    public IReadOnlyList<LogSeverity> Levels { get; init; } = [];

    /// <summary>Fragment searched for in the message or URL.</summary>
    public string? Search { get; init; }

    /// <summary>Exact HTTP method.</summary>
    public string? Method { get; init; }

    /// <summary>Exact HTTP status.</summary>
    public int? Status { get; init; }

    /// <summary>Lower bound of the window, included.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Upper bound of the window, excluded.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Sort from oldest to newest. The default is the reverse.</summary>
    public bool Ascending { get; init; }

    /// <summary>Brings pagination back within bounds.</summary>
    public LogQuery Normalized() => this with
    {
        Page = Math.Max(1, Page),
        PerPage = Math.Clamp(PerPage, 1, MaxPerPage),
    };
}
