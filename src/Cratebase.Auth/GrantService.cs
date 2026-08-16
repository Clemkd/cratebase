using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Schema;
using Dapper;

namespace Cratebase.Auth;

/// <summary>
/// Assigning roles and permissions to an account.
/// </summary>
/// <remarks>
/// A service separate from the CRUD engine, and reserved to superusers: privilege escalation must
/// not travel the same path as an ordinary field edit.
/// </remarks>
public sealed class GrantService(
    IDbConnectionFactory connections,
    CollectionRegistry registry,
    AuthTokenStore tokens,
    IClock clock)
{
    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    private readonly CollectionRegistry _registry = registry
        ?? throw new ArgumentNullException(nameof(registry));

    private readonly AuthTokenStore _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Sets an account's roles and permissions.</summary>
    public async Task AssignAsync(
        string collectionName,
        string recordId,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(permissions);

        var collection = _registry.Require(collectionName);

        if (collection.Kind is not CollectionKind.Auth)
        {
            throw new CratebaseBadRequestException(
                $"Collection \"{collection.Name}\" does not carry accounts.");
        }

        if (!RecordId.TryParse(recordId, out var id))
        {
            throw new CratebaseNotFoundException();
        }

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        var affected = await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 UPDATE {dialect.QuoteIdentifier(collection.TableName)}
                 SET {dialect.QuoteIdentifier(SystemFields.Roles)} = {Json(dialect, "@roles")},
                     {dialect.QuoteIdentifier(SystemFields.Permissions)} = {Json(dialect, "@permissions")},
                     {dialect.QuoteIdentifier(SystemFields.Updated)} = @now
                 WHERE {dialect.QuoteIdentifier(SystemFields.Id)} = @id
                 """,
                new
                {
                    roles = dialect.ToStorage(FieldType.Text, multiple: true, roles),
                    permissions = dialect.ToStorage(FieldType.Text, multiple: true, permissions),

                    // Date column of a records table: typed by the dialect, so converted by it. A
                    // canonical string is rejected by PostgreSQL.
                    now = dialect.ToStorage(FieldType.AutoDate, multiple: false, _clock.UtcNow),
                    id = id.ToString(),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (affected == 0)
        {
            throw new CratebaseNotFoundException();
        }

        static string Json(ISqlDialect dialect, string placeholder) =>
            dialect.BindParameter(FieldType.Text, multiple: true, placeholder);

        // Rights are resolved from the database on every request, so revoking tokens isn't
        // required for freshness. It's done anyway on a rights REMOVAL: an ongoing session must
        // stop being able to do what was just taken away, and this is the only way to be sure if a
        // cache is added later.
        await _tokens.RevokeAllAsync(collection.Name, id, cancellationToken).ConfigureAwait(false);
    }
}
