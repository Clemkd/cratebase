using System.Data.Common;
using System.Text.Json;
using Cratebase.Core;
using Cratebase.Data;
using Dapper;

namespace Cratebase.Schema;

/// <summary>
/// Persistance des définitions de collections.
/// </summary>
/// <remarks>
/// <para>
/// Les collections sont stockées <b>en tant que données</b>, dans une table système, et non en tant
/// que code. C'est ce qui rend le schéma portable : migrer de moteur consiste à relire cette table
/// et à régénérer le DDL pour la cible. C'est la règle R5 du document de conception, et c'est
/// l'avantage structurel du modèle à collections dynamiques.
/// </para>
/// </remarks>
public sealed class SchemaStore(IDbConnectionFactory connections)
{
    /// <summary>Table portant les définitions de collections.</summary>
    public const string CollectionsTable = "_collections";

    /// <summary>Table portant l'historique des migrations appliquées.</summary>
    public const string MigrationsTable = "_migrations";

    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    /// <summary>
    /// Crée les tables système si elles n'existent pas.
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

    /// <summary>Charge toutes les collections.</summary>
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

    /// <summary>Enregistre une collection, en création comme en modification.</summary>
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
            var sql = $"""
                       INSERT INTO {dialect.QuoteIdentifier(CollectionsTable)}
                         ({dialect.QuoteIdentifier("id")}, {dialect.QuoteIdentifier("name")},
                          {dialect.QuoteIdentifier("definition")}, {dialect.QuoteIdentifier("created")},
                          {dialect.QuoteIdentifier("updated")})
                       VALUES (@id, @name, @definition, @created, @updated)
                       ON CONFLICT ({dialect.QuoteIdentifier("id")}) DO UPDATE SET
                         {dialect.QuoteIdentifier("name")} = @name,
                         {dialect.QuoteIdentifier("definition")} = @definition,
                         {dialect.QuoteIdentifier("updated")} = @updated
                       """;

            await connection.ExecuteAsync(new CommandDefinition(
                    sql,
                    new
                    {
                        id = collection.Id.ToString(),
                        name = collection.Name,
                        definition = Serialize(collection),
                        created = Timestamp.Normalize(collection.Created),
                        updated = Timestamp.Normalize(collection.Updated),
                    },
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

    /// <summary>Supprime une définition de collection.</summary>
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

    /// <summary>Sérialise une définition pour le stockage et pour les instantanés de migration.</summary>
    public static string Serialize(CollectionDefinition collection) =>
        JsonSerializer.Serialize(collection, SchemaJson.Options);

    /// <summary>Relit une définition.</summary>
    public static CollectionDefinition Deserialize(string json) =>
        JsonSerializer.Deserialize<CollectionDefinition>(json, SchemaJson.Options)
        ?? throw new InvalidOperationException("Définition de collection illisible.");
}
