using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Schema;
using Dapper;

namespace Cratebase.Auth;

/// <summary>
/// Attribution des rôles et des permissions à un compte.
/// </summary>
/// <remarks>
/// Service distinct du moteur CRUD, et réservé au superadministrateur : l'élévation de privilèges
/// ne doit pas emprunter le même chemin que la modification d'un champ ordinaire.
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

    /// <summary>Pose les rôles et permissions d'un compte.</summary>
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
                $"La collection « {collection.Name} » ne porte pas de comptes.");
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

                    // Colonne de date d'une table d'enregistrements : typée par le dialecte, donc
                    // convertie par lui. Une chaîne canonique est refusée par PostgreSQL.
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

        // Les droits sont résolus à chaque requête depuis la base, donc la révocation des jetons
        // n'est pas indispensable à la fraîcheur. On la fait quand même sur un RETRAIT de droits :
        // une session en cours doit cesser de pouvoir ce qu'on vient de lui retirer, et c'est le
        // seul moyen d'en être sûr si un cache est ajouté plus tard.
        await _tokens.RevokeAllAsync(collection.Name, id, cancellationToken).ConfigureAwait(false);
    }
}
