using System.Globalization;
using Cratebase.Admin;
using Cratebase.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cratebase.Server;

/// <summary>
/// Log viewing endpoints.
/// </summary>
/// <remarks>
/// Reserved for the superuser: the log carries the called paths, callers' identifiers and,
/// depending on settings, their address. It's the most sensitive table in the instance after the
/// accounts table.
/// </remarks>
public static class LogEndpoints
{
    /// <summary>Default histogram window, in hours.</summary>
    private const int DefaultStatsWindowHours = 24;

    /// <summary>Publishes <c>/logs</c>.</summary>
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

            // The buffer is flushed before reading: without this, the error just triggered wouldn't
            // appear until the next pass of the background service, and the screen would look like
            // it lost it.
            await logs.FlushAsync(cancellationToken).ConfigureAwait(false);

            var query = ReadQuery(http);
            var page = await logs.QueryAsync(query, cancellationToken).ConfigureAwait(false);

            if (http.Request.Query["stats"] != "1")
            {
                return Results.Ok(page);
            }

            // ⚠️ The histogram is computed here, in the same request as the page, not through a
            // second call: two calls would each flush the buffer independently, so the second would
            // see entries the first didn't. The chart would then announce nineteen entries above a
            // table that counted seventeen — a screen contradicting itself.
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
                throw new CratebaseNotFoundException("Log entry not found.");
            }

            await logs.FlushAsync(cancellationToken).ConfigureAwait(false);

            var entry = await logs.GetAsync(recordId, cancellationToken).ConfigureAwait(false)
                ?? throw new CratebaseNotFoundException("Log entry not found.");

            return Results.Ok(entry);
        });

        group.MapDelete("/", async (
            LogStore logs,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            CollectionEndpoints.RequireSuperuser(user);

            var deleted = await logs.ClearAsync(cancellationToken).ConfigureAwait(false);

            // The purge logs itself: a log that can be cleared without leaving a trace of its own
            // clearing proves nothing anymore.
            logs.Record(LogSeverity.Warning, $"Log cleared: {deleted} entries deleted.");

            return Results.Ok(new { deleted });
        });

        return endpoints;
    }

    /// <summary>Formats the histogram for the client.</summary>
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
            // The slice is returned as a full instant: the client doesn't need to know that
            // grouping is done on a text prefix.
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
    /// Histogram granularity.
    /// </summary>
    /// <remarks>
    /// Inferred from the window when the client doesn't specify it: beyond two days, one point per
    /// hour produces hundreds of unreadable bars; below that, one point per day produces just one.
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
    /// Requested levels: <c>?level=Warning,Error</c>, or a repeated <c>?level=</c>.
    /// </summary>
    /// <remarks>
    /// An unknown name is ignored rather than rejected. The parameter comes from an address bar
    /// someone is tinkering with, and returning a 400 error on a typo would turn a query into an
    /// apparent outage; discarding the invalid value is enough.
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
