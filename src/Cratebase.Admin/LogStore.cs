using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Cratebase.Core;
using Cratebase.Data;
using Dapper;

namespace Cratebase.Admin;

/// <summary>
/// Request and error log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Buffered writes.</b> Logging on the request path would add a synchronous write to every
/// call — on SQLite, where writes are serialized, the log would become the API's own bottleneck,
/// the very thing it observes. Entries therefore go through a bounded channel, drained in batches
/// by a background service. It's PocketBase's trade-off, for the same reason.
/// </para>
/// <para>
/// <b>The channel is bounded and drops rather than waits.</b> An unbounded buffer would turn a
/// stalled database into memory exhaustion; a blocking write would make useful requests wait.
/// Drops are counted and shown in the console: a log that lies about its completeness is worse
/// than an incomplete log.
/// </para>
/// <para>
/// The table lives in the main database, not in a separate file like PocketBase: two databases
/// would force "where are the logs?" to be answered differently depending on the engine, and
/// PostgreSQL has no second file to open.
/// </para>
/// </remarks>
public sealed class LogStore(IDbConnectionFactory connections, IClock clock) : IDisposable
{
    /// <summary>Table holding the entries.</summary>
    public const string TableName = "_logs";

    private const int BufferCapacity = 4096;
    private const int BatchSize = 500;

    private static readonly IReadOnlyDictionary<string, object?> NoData =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private static readonly string[] Columns =
    [
        "id", "created", "level", "message", "method", "url", "status", "duration",
        "authCollection", "authId", "ip", "userAgent", "referer", "data",
    ];

    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly Channel<LogEntry> _pending = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    // Only one writer at a time: the background service and the log screen's own query can both
    // request a buffer flush at the same time.
    private readonly SemaphoreSlim _writing = new(1, 1);

    private long _dropped;

    /// <summary>Entries lost for lack of buffer space, since startup.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Creates the log table if it doesn't exist.</summary>
    public async Task EnsureTableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;
        var text = dialect.ColumnType(FieldType.Text, multiple: false);
        var number = dialect.ColumnType(FieldType.Number, multiple: false);

        string Column(string name, string type) => $"{dialect.QuoteIdentifier(name)} {type} NOT NULL";

        var statements = new[]
        {
            $"""
             CREATE TABLE IF NOT EXISTS {dialect.QuoteIdentifier(TableName)} (
               {dialect.QuoteIdentifier("id")} {text} NOT NULL PRIMARY KEY,
               {Column("created", text)},
               {Column("level", text)},
               {Column("message", text)},
               {Column("method", text)},
               {Column("url", text)},
               {Column("status", number)},
               {Column("duration", number)},
               {Column("authCollection", text)},
               {Column("authId", text)},
               {Column("ip", text)},
               {Column("userAgent", text)},
               {Column("referer", text)},
               {Column("data", text)}
             )
             """,

            // The time window is the first criterion of every query, and retention-based purging
            // sweeps that exact same column.
            $"""
             CREATE INDEX IF NOT EXISTS {dialect.QuoteIdentifier("idx_logs_created")}
               ON {dialect.QuoteIdentifier(TableName)} ({dialect.QuoteIdentifier("created")})
             """,
            $"""
             CREATE INDEX IF NOT EXISTS {dialect.QuoteIdentifier("idx_logs_level_created")}
               ON {dialect.QuoteIdentifier(TableName)}
               ({dialect.QuoteIdentifier("level")}, {dialect.QuoteIdentifier("created")})
             """,
        };

        foreach (var statement in statements)
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(statement, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enqueues an entry into the buffer. Never touches the database, so never blocks the caller.
    /// </summary>
    public void Record(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var stamped = entry.Created == default ? entry with { Created = _clock.UtcNow } : entry;

        if (!_pending.Writer.TryWrite(stamped))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Writes an application entry.</summary>
    public void Record(
        LogSeverity level,
        string message,
        IReadOnlyDictionary<string, object?>? data = null) =>
        Record(new LogEntry
        {
            Level = level,
            Message = message,
            Data = data ?? NoData,
        });

    /// <summary>
    /// Flushes the buffer into the database. Returns the number of entries written.
    /// </summary>
    /// <remarks>
    /// Called by the background service at regular intervals, and by the log screen's query:
    /// without this, the screen would always show the state from a few seconds ago, and an
    /// administrator who just triggered an error wouldn't find it.
    /// </remarks>
    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var batch = new List<LogEntry>(BatchSize);

            while (batch.Count < BatchSize && _pending.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count == 0)
            {
                return 0;
            }

            await WriteAsync(batch, cancellationToken).ConfigureAwait(false);

            return batch.Count;
        }
        finally
        {
            _writing.Release();
        }
    }

    private async Task WriteAsync(IReadOnlyList<LogEntry> batch, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;
        var columns = string.Join(", ", Columns.Select(dialect.QuoteIdentifier));
        var placeholders = string.Join(", ", Columns.Select(column => $"@{column}"));

        var sql = $"INSERT INTO {dialect.QuoteIdentifier(TableName)} ({columns}) VALUES ({placeholders})";

        // One batch, one transaction: on SQLite, that's the difference between one disk sync per
        // entry and a single one for the whole batch.
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var entry in batch)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    sql,
                    new
                    {
                        id = entry.Id.ToString(),
                        created = Timestamp.Normalize(entry.Created),
                        level = entry.Level.ToString(),
                        message = entry.Message,
                        method = entry.Method,
                        url = entry.Url,
                        status = (double)entry.Status,
                        duration = entry.Duration,
                        authCollection = entry.AuthCollection,
                        authId = entry.AuthId,
                        ip = entry.Ip,
                        userAgent = entry.UserAgent,
                        referer = entry.Referer,
                        data = JsonSerializer.Serialize(entry.Data, StorageJson.Options),
                    },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queries the log.</summary>
    public async Task<PagedResult<LogEntry>> QueryAsync(
        LogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var normalized = query.Normalized();
        var dialect = _connections.Dialect;
        var (where, parameters) = Predicate(normalized);

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT COUNT(*) FROM {dialect.QuoteIdentifier(TableName)}{where}",
                parameters,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var selection = string.Join(", ", Columns.Select(column =>
            $"{dialect.QuoteIdentifier(column)} AS {dialect.QuoteIdentifier(column)}"));

        // Sorting is done on "created", not on the identifier: the two coincide as long as entries
        // come from a single process, but two instances behind a load balancer don't share the same
        // generation clock — the visible order must stay the order of the instants.
        var order = normalized.Ascending ? "ASC" : "DESC";
        var offset = (normalized.Page - 1) * normalized.PerPage;

        var rows = await connection.QueryAsync<LogRow>(new CommandDefinition(
                $"""
                 SELECT {selection}
                 FROM {dialect.QuoteIdentifier(TableName)}{where}
                 ORDER BY {dialect.QuoteIdentifier("created")} {order},
                          {dialect.QuoteIdentifier("id")} {order}
                 {dialect.LimitOffset(normalized.PerPage, offset)}
                 """,
                parameters,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return PagedResult.Counted(
            normalized.Page,
            normalized.PerPage,
            total,
            [.. rows.Select(Materialize)]);
    }

    /// <summary>Loads an entry, or returns <see langword="null"/>.</summary>
    public async Task<LogEntry?> GetAsync(RecordId id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        var selection = string.Join(", ", Columns.Select(column =>
            $"{dialect.QuoteIdentifier(column)} AS {dialect.QuoteIdentifier(column)}"));

        var row = await connection.QueryFirstOrDefaultAsync<LogRow>(new CommandDefinition(
                $"""
                 SELECT {selection}
                 FROM {dialect.QuoteIdentifier(TableName)}
                 WHERE {dialect.QuoteIdentifier("id")} = @id
                 """,
                new { id = id.ToString() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null ? null : Materialize(row);
    }

    /// <summary>
    /// Counts entries by time slice and level.
    /// </summary>
    /// <remarks>
    /// Bucketing is done on the instant's <b>text prefix</b>, not with a date function: the
    /// canonical form has fixed length, so its first 13 characters designate the hour and its first
    /// 10 the day, identically across both engines. A truncation function would have required one
    /// more dialect method, hence one more entry in the conformance suite, for a result the format
    /// convention already gives for free.
    /// </remarks>
    public async Task<IReadOnlyList<LogBucket>> StatsAsync(
        LogQuery query,
        LogGranularity granularity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var dialect = _connections.Dialect;
        var (where, parameters) = Predicate(query);
        // 2026-08-13T14:05:09.123Z: ten characters for the day, thirteen for the hour, sixteen for
        // the minute.
        var length = granularity switch
        {
            LogGranularity.Minute => 16,
            LogGranularity.Hour => 13,
            _ => 10,
        };

        var bucket = $"substr({dialect.QuoteIdentifier("created")}, 1, {length})";

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<BucketRow>(new CommandDefinition(
                $"""
                 SELECT {bucket} AS {dialect.QuoteIdentifier("bucket")},
                        {dialect.QuoteIdentifier("level")} AS {dialect.QuoteIdentifier("level")},
                        COUNT(*) AS {dialect.QuoteIdentifier("count")}
                 FROM {dialect.QuoteIdentifier(TableName)}{where}
                 GROUP BY {bucket}, {dialect.QuoteIdentifier("level")}
                 ORDER BY {bucket} ASC
                 """,
                parameters,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return [.. rows.Select(row => new LogBucket(row.Bucket, ParseLevel(row.Level), row.Count))];
    }

    /// <summary>Total number of retained entries.</summary>
    public async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT COUNT(*) FROM {_connections.Dialect.QuoteIdentifier(TableName)}",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Deletes entries older than the retention period. Returns the count deleted.</summary>
    public async Task<int> PurgeAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0)
        {
            // Zero means "keep without limit". Treating this value as a cutoff date at the present
            // moment would empty the log on every pass of the background service.
            return 0;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        return await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {dialect.QuoteIdentifier(TableName)} " +
                $"WHERE {dialect.QuoteIdentifier("created")} < @cutoff",
                new { cutoff = Timestamp.Normalize(_clock.UtcNow.AddDays(-retentionDays)) },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Clears the log.</summary>
    public async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        // The buffer goes with the rest: leaving pending entries would make them reappear a few
        // seconds after a purge the administrator believes is finished.
        while (_pending.Reader.TryRead(out _))
        {
        }

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {_connections.Dialect.QuoteIdentifier(TableName)}",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _writing.Dispose();

    private (string Where, DynamicParameters Parameters) Predicate(LogQuery query)
    {
        var dialect = _connections.Dialect;
        var parameters = new DynamicParameters();
        var conditions = new List<string>();

        // A full set restricts nothing: letting it through would pay for a four-value `IN` on a
        // filter that discards no row.
        if (query.Levels.Count > 0 && query.Levels.Count < LogSeverities.All.Count)
        {
            // Levels are stored by name: this is therefore where, in C#, the retained set is
            // decided. Storing the rank in the database would have shifted the entire history the
            // day a level gets inserted in the middle.
            var levels = query.Levels.Distinct().Select(level => level.ToString()).ToArray();

            conditions.Add($"{dialect.QuoteIdentifier("level")} IN @levels");
            parameters.Add("levels", levels);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var message = dialect.LikeExpression(dialect.QuoteIdentifier("message"), "@search", negated: false);
            var url = dialect.LikeExpression(dialect.QuoteIdentifier("url"), "@search", negated: false);

            conditions.Add($"({message} OR {url})");
            parameters.Add("search", LikePattern.Contains(query.Search.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.Method))
        {
            conditions.Add($"{dialect.QuoteIdentifier("method")} = @method");
            parameters.Add("method", query.Method.Trim().ToUpperInvariant());
        }

        if (query.Status is { } status)
        {
            conditions.Add($"{dialect.QuoteIdentifier("status")} = @status");
            parameters.Add("status", (double)status);
        }

        if (query.From is { } from)
        {
            conditions.Add($"{dialect.QuoteIdentifier("created")} >= @from");
            parameters.Add("from", Timestamp.Normalize(from));
        }

        if (query.To is { } to)
        {
            conditions.Add($"{dialect.QuoteIdentifier("created")} < @to");
            parameters.Add("to", Timestamp.Normalize(to));
        }

        return (conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions), parameters);
    }

    private static LogEntry Materialize(LogRow row) => new()
    {
        Id = RecordId.TryParse(row.Id, out var id) ? id : RecordId.Empty,
        Created = Timestamp.Parse(row.Created),
        Level = ParseLevel(row.Level),
        Message = row.Message,
        Method = row.Method,
        Url = row.Url,
        Status = (int)Math.Round(row.Status, MidpointRounding.AwayFromZero),
        Duration = row.Duration,
        AuthCollection = row.AuthCollection,
        AuthId = row.AuthId,
        Ip = row.Ip,
        UserAgent = row.UserAgent,
        Referer = row.Referer,
        Data = ReadData(row.Data),
    };

    private static LogSeverity ParseLevel(string raw) =>
        Enum.TryParse<LogSeverity>(raw, ignoreCase: true, out var level) ? level : LogSeverity.Info;

    private static IReadOnlyDictionary<string, object?> ReadData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return NoData;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, StorageJson.Options);

            return parsed is null
                ? NoData
                : parsed.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // An entry written by an earlier version must not make the log screen unusable: the
            // payload is returned as-is, leaving it to the human to read.
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["raw"] = json };
        }
    }

    private sealed record LogRow
    {
        public string Id { get; init; } = string.Empty;

        public string Created { get; init; } = Timestamp.Normalize(DateTimeOffset.UnixEpoch);

        public string Level { get; init; } = nameof(LogSeverity.Info);

        public string Message { get; init; } = string.Empty;

        public string Method { get; init; } = string.Empty;

        public string Url { get; init; } = string.Empty;

        public double Status { get; init; }

        public double Duration { get; init; }

        public string AuthCollection { get; init; } = string.Empty;

        public string AuthId { get; init; } = string.Empty;

        public string Ip { get; init; } = string.Empty;

        public string UserAgent { get; init; } = string.Empty;

        public string Referer { get; init; } = string.Empty;

        public string Data { get; init; } = string.Empty;
    }

    private sealed record BucketRow
    {
        public string Bucket { get; init; } = string.Empty;

        public string Level { get; init; } = nameof(LogSeverity.Info);

        public long Count { get; init; }
    }
}

/// <summary>Formatting of histogram slices.</summary>
public static class LogBuckets
{
    /// <summary>
    /// Renders a slice start as an instant. The canonical prefix is completed with zeros, since it
    /// designates the start of the hour or the day.
    /// </summary>
    public static DateTimeOffset ToInstant(string bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);

        var completed = bucket.Length switch
        {
            10 => bucket + "T00:00:00.000Z",
            13 => bucket + ":00:00.000Z",
            16 => bucket + ":00.000Z",
            _ => bucket,
        };

        return DateTimeOffset.TryParse(
            completed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
    }
}
