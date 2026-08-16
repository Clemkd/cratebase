using System.Text.Json;
using Cratebase.Core;
using Cratebase.Data;
using Dapper;

namespace Cratebase.Admin;

/// <summary>
/// Persisted instance settings.
/// </summary>
/// <remarks>
/// <para>
/// A single row whose value is a JSON document, rather than one column per setting: adding a
/// setting would otherwise become a schema migration, on a table nobody queries other than by its
/// key. This is the same trade-off as PocketBase's <c>_params</c> table.
/// </para>
/// <para>
/// The current value is kept in memory: it's read on every request by the logging middleware, and
/// a database read per request for three booleans would be absurd. The cache is written by
/// <see cref="SaveAsync"/>, so one instance doesn't see settings changed by another — an accepted
/// limit as long as the reference deployment fits in a single container.
/// </para>
/// </remarks>
public sealed class SettingsStore(IDbConnectionFactory connections, IClock clock)
{
    /// <summary>Table holding the settings.</summary>
    public const string TableName = "_settings";

    /// <summary>Key of the single row.</summary>
    private const string RowKey = "app";

    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private volatile AppSettings _current = new();

    /// <summary>Settings currently in effect.</summary>
    public AppSettings Current => _current;

    /// <summary>Creates the settings table if it doesn't exist.</summary>
    public async Task EnsureTableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;
        var text = dialect.ColumnType(FieldType.Text, multiple: false);

        await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 CREATE TABLE IF NOT EXISTS {dialect.QuoteIdentifier(TableName)} (
                   {dialect.QuoteIdentifier("key")} {text} NOT NULL PRIMARY KEY,
                   {dialect.QuoteIdentifier("value")} {text} NOT NULL,
                   {dialect.QuoteIdentifier("updated")} {text} NOT NULL
                 )
                 """,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Re-reads settings from the database and refreshes the cache.</summary>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        var stored = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                $"SELECT {dialect.QuoteIdentifier("value")} FROM {dialect.QuoteIdentifier(TableName)} " +
                $"WHERE {dialect.QuoteIdentifier("key")} = @key",
                new { key = RowKey },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        _current = Deserialize(stored);

        return _current;
    }

    /// <summary>Validates, persists, and publishes new settings.</summary>
    public async Task<AppSettings> SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var validated = settings.Validated();
        var payload = JsonSerializer.Serialize(validated, StorageJson.Options);
        var now = Timestamp.Normalize(_clock.UtcNow);

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        // Update, then insert if nothing changed: "INSERT ... ON CONFLICT" is written differently
        // per engine, and this table is written once per settings change — the race has no
        // observable consequence here.
        var updated = await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 UPDATE {dialect.QuoteIdentifier(TableName)}
                 SET {dialect.QuoteIdentifier("value")} = @value,
                     {dialect.QuoteIdentifier("updated")} = @updated
                 WHERE {dialect.QuoteIdentifier("key")} = @key
                 """,
                new { key = RowKey, value = payload, updated = now },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (updated == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    $"""
                     INSERT INTO {dialect.QuoteIdentifier(TableName)}
                       ({dialect.QuoteIdentifier("key")}, {dialect.QuoteIdentifier("value")},
                        {dialect.QuoteIdentifier("updated")})
                     VALUES (@key, @value, @updated)
                     """,
                    new { key = RowKey, value = payload, updated = now },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        _current = validated;

        return validated;
    }

    private static AppSettings Deserialize(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(stored, StorageJson.Options) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Unreadable settings — written by a future version, or corrupted. The default values
            // let the instance start; refusing to start would make the console unreachable, and the
            // problem unfixable from the interface.
            return new AppSettings();
        }
    }
}
