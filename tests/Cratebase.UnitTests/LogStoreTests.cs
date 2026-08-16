using Cratebase.Admin;
using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Data.Sqlite;
using Shouldly;

namespace Cratebase.UnitTests;

/// <summary>
/// The log is the only table written outside the request path: its entries pass through a
/// buffer, and anything lost there is lost silently. Hence tests that exercise the full cycle —
/// record, flush, read back — rather than just the SQL queries.
/// </summary>
public sealed class LogStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"cratebase-logs-{Guid.NewGuid():N}.db");

    private readonly FixedClock _clock = FixedClock.Default;

    private LogStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var connections = new DbConnectionFactory(
            SqliteDialect.Instance,
            SqliteDialect.Instance,
            $"Data Source={_path}");

        _store = new LogStore(connections, _clock);

        await _store.EnsureTableAsync(Token);
    }

    public ValueTask DisposeAsync()
    {
        _store.Dispose();

        // Connections are closed after every operation, but the driver keeps a pool: without a
        // purge, the file stays held open and can't be deleted.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);

        return ValueTask.CompletedTask;
    }

    private LogEntry Request(string method, string url, int status, DateTimeOffset? at = null) => new()
    {
        Created = at ?? _clock.UtcNow,
        Level = LogSeverities.ForStatus(status),
        Message = $"{method} {url} → {status}",
        Method = method,
        Url = url,
        Status = status,
        Duration = 12.5,
    };

    [Fact]
    public async Task A_recorded_entry_is_read_back_identically()
    {
        _store.Record(Request("GET", "/api/collections", 200));

        (await _store.FlushAsync(Token)).ShouldBe(1);

        var page = await _store.QueryAsync(new LogQuery(), Token);
        var entry = page.Items.ShouldHaveSingleItem();

        entry.Method.ShouldBe("GET");
        entry.Url.ShouldBe("/api/collections");
        entry.Status.ShouldBe(200);
        entry.Level.ShouldBe(LogSeverity.Info);
        entry.Duration.ShouldBe(12.5);
        entry.Created.ShouldBe(_clock.UtcNow);
    }

    [Fact]
    public async Task Nothing_is_written_until_the_buffer_is_flushed()
    {
        _store.Record(Request("GET", "/api/collections", 200));

        (await _store.CountAsync(Token)).ShouldBe(0);

        await _store.FlushAsync(Token);

        (await _store.CountAsync(Token)).ShouldBe(1);
    }

    [Fact]
    public async Task The_level_filter_keeps_exactly_the_requested_set()
    {
        _store.Record(Request("GET", "/api/ok", 200));
        _store.Record(Request("GET", "/api/missing", 404));
        _store.Record(Request("POST", "/api/broken", 500));

        await _store.FlushAsync(Token);

        var severe = await _store.QueryAsync(
            new LogQuery { Levels = [LogSeverity.Warning, LogSeverity.Error] },
            Token);

        severe.TotalItems.ShouldBe(2);
        severe.Items.Select(entry => entry.Status).ShouldBe([500, 404], ignoreOrder: true);

        var errors = await _store.QueryAsync(new LogQuery { Levels = [LogSeverity.Error] }, Token);

        errors.Items.ShouldHaveSingleItem().Status.ShouldBe(500);

        // What a single lower bound couldn't express: warnings without errors.
        var warningsOnly = await _store.QueryAsync(
            new LogQuery { Levels = [LogSeverity.Warning] },
            Token);

        warningsOnly.Items.ShouldHaveSingleItem().Status.ShouldBe(404);
    }

    [Fact]
    public async Task An_empty_or_full_set_filters_nothing()
    {
        _store.Record(Request("GET", "/api/ok", 200));
        _store.Record(Request("POST", "/api/broken", 500));

        await _store.FlushAsync(Token);

        (await _store.QueryAsync(new LogQuery(), Token)).TotalItems.ShouldBe(2);

        (await _store.QueryAsync(new LogQuery { Levels = LogSeverities.All }, Token))
            .TotalItems.ShouldBe(2);
    }

    [Fact]
    public async Task Search_covers_both_the_message_and_the_url()
    {
        _store.Record(Request("GET", "/api/collections/posts/records", 200));
        _store.Record(Request("GET", "/api/collections/users/records", 200));

        await _store.FlushAsync(Token);

        var found = await _store.QueryAsync(new LogQuery { Search = "posts" }, Token);

        found.Items.ShouldHaveSingleItem().Url.ShouldContain("posts");
    }

    [Fact]
    public async Task Search_wildcards_are_escaped()
    {
        _store.Record(Request("GET", "/api/collections", 200));

        await _store.FlushAsync(Token);

        // Without escaping, "%_%" would bring back every row: the most discreet way to make a
        // search look like it found something.
        var page = await _store.QueryAsync(new LogQuery { Search = "%_%" }, Token);

        page.TotalItems.ShouldBe(0);
    }

    [Fact]
    public async Task The_time_window_bounds_both_the_page_and_the_histogram()
    {
        var now = _clock.UtcNow;

        _store.Record(Request("GET", "/api/recent", 200, now));
        _store.Record(Request("GET", "/api/old", 200, now.AddDays(-3)));

        await _store.FlushAsync(Token);

        var recent = await _store.QueryAsync(new LogQuery { From = now.AddHours(-1) }, Token);

        recent.Items.ShouldHaveSingleItem().Url.ShouldBe("/api/recent");

        var buckets = await _store.StatsAsync(
            new LogQuery { From = now.AddHours(-1) },
            LogGranularity.Hour,
            Token);

        buckets.ShouldHaveSingleItem().Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_histogram_groups_by_bucket_and_by_level()
    {
        var hour = _clock.UtcNow;

        _store.Record(Request("GET", "/api/a", 200, hour));
        _store.Record(Request("GET", "/api/b", 200, hour.AddMinutes(10)));
        _store.Record(Request("GET", "/api/c", 500, hour.AddMinutes(20)));

        await _store.FlushAsync(Token);

        var buckets = await _store.StatsAsync(new LogQuery(), LogGranularity.Hour, Token);

        buckets.Count.ShouldBe(2);
        buckets.Single(bucket => bucket.Level == LogSeverity.Info).Count.ShouldBe(2);
        buckets.Single(bucket => bucket.Level == LogSeverity.Error).Count.ShouldBe(1);

        // The hourly bucket is the canonical prefix: the same thirteen characters on both sides.
        buckets[0].Bucket.ShouldBe("2026-08-13T14");
    }

    [Fact]
    public async Task Purge_removes_entries_beyond_the_retention_window()
    {
        _store.Record(Request("GET", "/api/old", 200, _clock.UtcNow.AddDays(-10)));
        _store.Record(Request("GET", "/api/fresh", 200, _clock.UtcNow));

        await _store.FlushAsync(Token);

        (await _store.PurgeAsync(7, Token)).ShouldBe(1);
        (await _store.CountAsync(Token)).ShouldBe(1);
    }

    [Fact]
    public async Task A_zero_retention_removes_nothing()
    {
        _store.Record(Request("GET", "/api/old", 200, _clock.UtcNow.AddDays(-400)));

        await _store.FlushAsync(Token);

        // Zero means "keep without limit". Treating it as a cutoff at the present would empty the
        // log every time the maintenance service runs.
        (await _store.PurgeAsync(0, Token)).ShouldBe(0);
        (await _store.CountAsync(Token)).ShouldBe(1);
    }

    [Fact]
    public async Task Clearing_the_log_also_takes_the_buffer_with_it()
    {
        _store.Record(Request("GET", "/api/written", 200));
        await _store.FlushAsync(Token);

        _store.Record(Request("GET", "/api/pending", 200));

        (await _store.ClearAsync(Token)).ShouldBe(1);
        await _store.FlushAsync(Token);

        // Without purging the buffer, the pending entry would reappear a few seconds after a
        // clear the administrator believes is complete.
        (await _store.CountAsync(Token)).ShouldBe(0);
    }

    [Fact]
    public async Task Pagination_returns_the_most_recent_entries_first()
    {
        for (var index = 0; index < 5; index += 1)
        {
            _store.Record(Request("GET", $"/api/{index}", 200, _clock.UtcNow.AddMinutes(index)));
        }

        await _store.FlushAsync(Token);

        var page = await _store.QueryAsync(new LogQuery { PerPage = 2 }, Token);

        page.TotalItems.ShouldBe(5);
        page.TotalPages.ShouldBe(3);
        page.Items.Select(entry => entry.Url).ShouldBe(["/api/4", "/api/3"]);

        var ascending = await _store.QueryAsync(new LogQuery { PerPage = 2, Ascending = true }, Token);

        ascending.Items.Select(entry => entry.Url).ShouldBe(["/api/0", "/api/1"]);
    }

    [Fact]
    public async Task Free_form_details_survive_the_round_trip()
    {
        _store.Record(
            LogSeverity.Error,
            "Unreadable settings",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["exception"] = "JsonException",
                ["attempts"] = 3,
            });

        await _store.FlushAsync(Token);

        var entry = (await _store.QueryAsync(new LogQuery(), Token)).Items.ShouldHaveSingleItem();

        entry.Message.ShouldBe("Unreadable settings");
        entry.Data["exception"]?.ToString().ShouldBe("JsonException");
        entry.Data["attempts"]?.ToString().ShouldBe("3");

        // An application entry doesn't correspond to any request: no method, no status, and
        // above all no NULL that would propagate through filters.
        entry.Method.ShouldBe(string.Empty);
        entry.Status.ShouldBe(0);
    }
}
