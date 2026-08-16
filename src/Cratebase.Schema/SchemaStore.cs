using System.Data.Common;
using System.Text.Json;
using Cratebase.Core;
using Cratebase.Data;
using Dapper;

namespace Cratebase.Schema;

/// <summary>
/// Persistence of collection definitions.
/// </summary>
/// <remarks>
/// <para>
/// Collections are stored <b>as data</b>, in a system table, not as code. This is what makes the
/// schema portable: migrating engines means re-reading this table and regenerating the DDL for the
/// target. This is rule R5 of the design document, and the structural advantage of the
/// dynamic-collection model.
/// </para>
/// </remarks>
public sealed class SchemaStore(IDbConnectionFactory connections)
{
    /// <summary>Table carrying collection definitions.</summary>
    public const string CollectionsTable = "_collections";

    /// <summary>Table carrying the history of applied migrations.</summary>
    public const string MigrationsTable = "_migrations";

    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    /// <summary>
    /// Creates system tables if they don't exist.
    /// </summary>
    public async Task EnsureSystemTablesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;
        var text = dialect.ColumnType(FieldType.Text, multiple: false);

        var statements = new[]
        {
            $"""
             CREATE TABLE IF NOT EXISTS {dialect.QuoteIdentifier(CollectionsTable)} (
               {dialect.QuoteIdentifier("id")} {text} NOT NULL PRIMARY KEY,
               {dialect.QuoteIdentifier("name")} {text} NOT NULL,
               {dialect.QuoteIdentifier("definition")} {text} NOT NULL,
               {dialect.QuoteIdentifier("created")} {text} NOT NULL,
               {dialect.QuoteIdentifier("updated")} {text} NOT NULL
             )
             """,
            $"""
             CREATE UNIQUE INDEX IF NOT EXISTS {dialect.QuoteIdentifier("idx_collections_name")}
               ON {dialect.QuoteIdentifier(CollectionsTable)} ({dialect.QuoteIdentifier("name")})
             """,
            $"""
             CREATE TABLE IF NOT EXISTS {dialect.QuoteIdentifier(MigrationsTable)} (
               {dialect.QuoteIdentifier("name")} {text} NOT NULL PRIMARY KEY,
               {dialect.QuoteIdentifier("applied")} {text} NOT NULL
             )
             """,
        };

        foreach (var statement in statements)
        {
            await connection.ExecuteAsync(new CommandDefinition(statement, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    /// <summary>Loads every collection.</summary>
    public async Task<IReadOnlyList<CollectionDefinition>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        var rows = await connection.QueryAsync<string>(new CommandDefinition(
                $"SELECT {dialect.QuoteIdentifier("definition")} " +
                $"FROM {dialect.QuoteIdentifier(CollectionsTable)} " +
                $"ORDER BY {dialect.QuoteIdentifier("name")}",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return [.. rows.Select(Deserialize)];
    }

    /// <summary>Saves a collection, on both creation and modification.</summary>
    public async Task SaveAsync(
        CollectionDefinition collection,
        DbConnection? existing = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var dialect = _connections.Dialect;
        var owned = existing is null;
        var connection = existing ?? await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var id = collection.Id.ToString();
            var definition = Serialize(collection);
            var updatedAt = Timestamp.Normalize(collection.Updated);

            // Update then insert if nothing moved, rather than an "INSERT ... ON CONFLICT": the
            // upsert clause is dialect-specific, and rule R1 forbids it from appearing here. Calls
            // that carry a transaction — CollectionRegistry's do — make the sequence atomic; the
            // others write one definition at a time, on an administration action.
            var updated = await connection.ExecuteAsync(new CommandDefinition(
                    $"""
                     UPDATE {dialect.QuoteIdentifier(CollectionsTable)}
                     SET {dialect.QuoteIdentifier("name")} = @name,
                         {dialect.QuoteIdentifier("definition")} = @definition,
                         {dialect.QuoteIdentifier("updated")} = @updated
                     WHERE {dialect.QuoteIdentifier("id")} = @id
                     """,
                    new { id, name = collection.Name, definition, updated = updatedAt },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            if (updated == 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                        $"""
                         INSERT INTO {dialect.QuoteIdentifier(CollectionsTable)}
                           ({dialect.QuoteIdentifier("id")}, {dialect.QuoteIdentifier("name")},
                            {dialect.QuoteIdentifier("definition")}, {dialect.QuoteIdentifier("created")},
                            {dialect.QuoteIdentifier("updated")})
                         VALUES (@id, @name, @definition, @created, @updated)
                         """,
                        new
                        {
                            id,
                            name = collection.Name,
                            definition,
                            created = Timestamp.Normalize(collection.Created),
                            updated = updatedAt,
                        },
                        transaction,
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (owned)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Deletes a collection definition.</summary>
    public async Task DeleteAsync(
        RecordId id,
        DbConnection? existing = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var dialect = _connections.Dialect;
        var owned = existing is null;
        var connection = existing ?? await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    $"DELETE FROM {dialect.QuoteIdentifier(CollectionsTable)} " +
                    $"WHERE {dialect.QuoteIdentifier("id")} = @id",
                    new { id = id.ToString() },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        finally
        {
            if (owned)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Serializes a definition for storage and for migration snapshots.</summary>
    public static string Serialize(CollectionDefinition collection) =>
        JsonSerializer.Serialize(collection, SchemaJson.Options);

    /// <summary>Reads back a definition.</summary>
    public static CollectionDefinition Deserialize(string json) =>
        JsonSerializer.Deserialize<CollectionDefinition>(json, SchemaJson.Options)
        ?? throw new InvalidOperationException("Unreadable collection definition.");
}
