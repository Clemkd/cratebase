using System.Data.Common;
using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Schema;
using Dapper;

namespace Cratebase.Auth;

/// <summary>Résultat d'une authentification.</summary>
/// <param name="Token">
/// Jeton à présenter en <c>Authorization</c>, ou <see langword="null"/> si un second facteur reste
/// à fournir.
/// </param>
/// <param name="Record">Enregistrement authentifié, champs masqués retirés.</param>
/// <param name="MfaId">
/// Identifiant du défi de double authentification, quand le premier facteur a réussi mais ne
/// suffit pas. <b>Aucun jeton n'est émis dans ce cas</b> — c'est ce qui distingue une vraie double
/// authentification d'un écran de confirmation cosmétique.
/// </param>
public sealed record AuthResult(
    string? Token,
    IReadOnlyDictionary<string, object?> Record,
    string? MfaId = null);

/// <summary>
/// Authentification par identifiant et mot de passe, et résolution des droits.
/// </summary>
public sealed class AuthService(
    IDbConnectionFactory connections,
    CollectionRegistry registry,
    AuthTokenStore tokens,
    MfaService mfa,
    IClock clock)
{
    /// <summary>Nom de la collection des superadministrateurs.</summary>
    public const string SuperusersCollection = "_superusers";

    /// <summary>Nom de la collection des rôles.</summary>
    public const string RolesCollection = "_roles";

    /// <summary>
    /// Nom du champ portant les permissions d'un rôle.
    /// </summary>
    /// <remarks>
    /// Volontairement distinct de <see cref="SystemFields.Permissions"/> : ce dernier est un nom
    /// réservé, que le moteur retire des collections où il n'a pas cours. Réutiliser le même nom
    /// ferait disparaître le champ des rôles à la première normalisation, sans erreur.
    /// </remarks>
    public const string RoleGrantsField = "grants";

    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    private readonly CollectionRegistry _registry = registry
        ?? throw new ArgumentNullException(nameof(registry));

    private readonly AuthTokenStore _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private readonly MfaService _mfa = mfa ?? throw new ArgumentNullException(nameof(mfa));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Authentifie un compte.
    /// </summary>
    /// <remarks>
    /// Le message d'échec est <b>le même</b> qu'il s'agisse d'une adresse inconnue ou d'un mot de
    /// passe faux, et un condensat factice est vérifié dans le premier cas : sans cela, la
    /// différence de temps de réponse dit à l'attaquant quelles adresses existent.
    /// </remarks>
    public async Task<AuthResult> AuthenticateAsync(
        string collectionName,
        string identity,
        string password,
        CancellationToken cancellationToken = default)
    {
        var collection = _registry.Require(collectionName);

        if (collection.Kind is not CollectionKind.Auth)
        {
            throw new CratebaseBadRequestException(
                $"La collection « {collection.Name} » n'est pas une collection d'authentification.");
        }

        var record = await FindByEmailAsync(collection, identity, cancellationToken).ConfigureAwait(false);
        var hash = record?.GetValueOrDefault(SystemFields.Password) as string;

        if (!PasswordHasher.Verify(password, hash ?? DecoyHash))
        {
            throw new CratebaseBadRequestException("Identifiant ou mot de passe incorrect.");
        }

        if (record is null || !RecordId.TryParse(record.GetValueOrDefault(SystemFields.Id) as string, out var id))
        {
            throw new CratebaseBadRequestException("Identifiant ou mot de passe incorrect.");
        }

        // Le mot de passe est bon, mais il ne suffit pas : on rend un défi, pas un jeton.
        if (await _mfa.IsEnabledAsync(collection.Name, id, cancellationToken).ConfigureAwait(false))
        {
            var challenge = await _mfa.ChallengeAsync(collection.Name, id, cancellationToken)
                .ConfigureAwait(false);

            return new AuthResult(null, Sanitize(collection, record), challenge);
        }

        var token = await _tokens
            .IssueAsync(collection.Name, id, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new AuthResult(token, Sanitize(collection, record));
    }

    /// <summary>Charge un enregistrement d'auth et ses permissions effectives.</summary>
    public async Task<AuthenticatedRecord?> LoadAsync(
        string collectionName,
        RecordId recordId,
        CancellationToken cancellationToken = default)
    {
        var collection = _registry.Find(collectionName);

        if (collection is null || collection.Kind is not CollectionKind.Auth)
        {
            return null;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        var row = await connection.QueryFirstOrDefaultAsync(new CommandDefinition(
                $"SELECT * FROM {dialect.QuoteIdentifier(collection.TableName)} " +
                $"WHERE {dialect.QuoteIdentifier(SystemFields.Id)} = @id",
                new { id = recordId.ToString() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var values = Materialize(collection, (IDictionary<string, object?>)row);
        var permissions = await ResolvePermissionsAsync(values, cancellationToken).ConfigureAwait(false);

        return new AuthenticatedRecord(
            collection.Name,
            recordId,
            string.Equals(collection.Name, SuperusersCollection, StringComparison.Ordinal),
            permissions,
            Sanitize(collection, values));
    }

    /// <summary>
    /// Calcule les permissions effectives : celles des rôles, plus les dérogations individuelles.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> ResolvePermissionsAsync(
        IReadOnlyDictionary<string, object?> record,
        CancellationToken cancellationToken)
    {
        var effective = new HashSet<string>(StringComparer.Ordinal);

        foreach (var permission in AsList(record.GetValueOrDefault(SystemFields.Permissions)))
        {
            effective.Add(permission);
        }

        var roles = AsList(record.GetValueOrDefault(SystemFields.Roles));

        if (roles.Count == 0 || _registry.Find(RolesCollection) is not { } rolesCollection)
        {
            return effective;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        // Emplacements générés un par un, plutôt que de compter sur l'expansion automatique de
        // « IN @names » par Dapper : elle dépend du pilote, et là où elle n'opère pas, le tableau
        // part comme paramètre unique et le moteur rejette la requête. Explicite, donc portable.
        var placeholders = new List<string>(roles.Count);
        var parameters = new DynamicParameters();

        for (var index = 0; index < roles.Count; index++)
        {
            placeholders.Add($"@role{index}");
            parameters.Add($"role{index}", roles[index]);
        }

        var rows = await connection.QueryAsync<string>(new CommandDefinition(
                $"""
                 SELECT {dialect.QuoteIdentifier(RoleGrantsField)}
                 FROM {dialect.QuoteIdentifier(rolesCollection.TableName)}
                 WHERE {dialect.QuoteIdentifier("name")} IN ({string.Join(", ", placeholders)})
                 """,
                parameters,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        foreach (var payload in rows)
        {
            foreach (var permission in StorageJson.DeserializeMultiple(payload))
            {
                effective.Add(permission);
            }
        }

        return effective;
    }

    /// <summary>
    /// Crée ou met à jour un superadministrateur.
    /// </summary>
    /// <remarks>
    /// Sert au démarrage initial (variables d'environnement) et à la ligne de commande. Volontaire-
    /// ment hors de l'API des enregistrements : un compte superadmin ne se crée pas par un POST.
    /// </remarks>
    public async Task UpsertSuperuserAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        if (password is not { Length: >= PasswordHasher.MinimumLength })
        {
            throw new CratebaseValidationException(new Dictionary<string, string[]>
            {
                [SystemFields.Password] =
                    [$"Le mot de passe doit compter au moins {PasswordHasher.MinimumLength} caractères."],
            });
        }

        var collection = _registry.Require(SuperusersCollection);
        var existing = await FindByEmailAsync(collection, email, cancellationToken).ConfigureAwait(false);

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        // ⚠️ Passer par ToStorage, et non par la chaîne canonique directement. Les colonnes de date
        // d'une table d'enregistrements sont typées par le dialecte : « TEXT » sur SQLite, mais
        // « timestamptz » sur PostgreSQL, qui refuse net une chaîne. Écrire la date en dur marche
        // donc jusqu'au jour de la migration, et pas au-delà.
        var now = dialect.ToStorage(FieldType.AutoDate, multiple: false, _clock.UtcNow);
        var hash = PasswordHasher.Hash(password);

        if (existing is not null
            && RecordId.TryParse(existing.GetValueOrDefault(SystemFields.Id) as string, out var existingId))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    $"""
                     UPDATE {dialect.QuoteIdentifier(collection.TableName)}
                     SET {dialect.QuoteIdentifier(SystemFields.Password)} = @password,
                         {dialect.QuoteIdentifier(SystemFields.Updated)} = @now
                     WHERE {dialect.QuoteIdentifier(SystemFields.Id)} = @id
                     """,
                    new { password = hash, now, id = existingId.ToString() },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            // Changer le mot de passe déconnecte les sessions ouvertes. C'est le réflexe attendu
            // après un vol de session, et il serait sans effet sans cette ligne.
            await _tokens.RevokeAllAsync(collection.Name, existingId, cancellationToken).ConfigureAwait(false);

            return;
        }

        var id = RecordId.New();

        await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 INSERT INTO {dialect.QuoteIdentifier(collection.TableName)}
                   ({dialect.QuoteIdentifier(SystemFields.Id)},
                    {dialect.QuoteIdentifier(SystemFields.Created)},
                    {dialect.QuoteIdentifier(SystemFields.Updated)},
                    {dialect.QuoteIdentifier(SystemFields.Email)},
                    {dialect.QuoteIdentifier(SystemFields.EmailVisibility)},
                    {dialect.QuoteIdentifier(SystemFields.Verified)},
                    {dialect.QuoteIdentifier(SystemFields.Password)},
                    {dialect.QuoteIdentifier(SystemFields.TokenKey)},
                    {dialect.QuoteIdentifier(SystemFields.Roles)},
                    {dialect.QuoteIdentifier(SystemFields.Permissions)})
                 VALUES (@id, @now, @now, @email, @visible, @verified, @password, @tokenKey,
                         {dialect.BindParameter(FieldType.Text, true, "@roles")},
                         {dialect.BindParameter(FieldType.Text, true, "@permissions")})
                 """,
                new
                {
                    id = id.ToString(),
                    now,
                    email,
                    visible = dialect.ToStorage(FieldType.Bool, false, false),
                    verified = dialect.ToStorage(FieldType.Bool, false, true),
                    password = hash,
                    tokenKey = RecordId.New().ToString(),
                    // Toujours par le dialecte : sur PostgreSQL, ces colonnes sont en jsonb et
                    // refusent une chaîne nue.
                    roles = dialect.ToStorage(FieldType.Text, multiple: true, null),
                    permissions = dialect.ToStorage(FieldType.Text, multiple: true, null),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Y a-t-il au moins un superadministrateur ?</summary>
    public async Task<bool> HasSuperuserAsync(CancellationToken cancellationToken = default)
    {
        if (_registry.Find(SuperusersCollection) is not { } collection)
        {
            return false;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT COUNT(*) FROM {_connections.Dialect.QuoteIdentifier(collection.TableName)}",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return count > 0;
    }

    private async Task<IReadOnlyDictionary<string, object?>?> FindByEmailAsync(
        CollectionDefinition collection,
        string email,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        var row = await connection.QueryFirstOrDefaultAsync(new CommandDefinition(
                $"SELECT * FROM {dialect.QuoteIdentifier(collection.TableName)} " +
                $"WHERE {dialect.QuoteIdentifier(SystemFields.Email)} = @email",
                new { email },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null ? null : Materialize(collection, (IDictionary<string, object?>)row);
    }

    private Dictionary<string, object?> Materialize(
        CollectionDefinition collection,
        IDictionary<string, object?> row)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var field in collection.Fields)
        {
            if (row.TryGetValue(field.ColumnName, out var stored))
            {
                result[field.Name] = field.Name is SystemFields.Password
                    ? stored
                    : _connections.Dialect.FromStorage(field.Type, field.Multiple, stored);
            }
        }

        return result;
    }

    private static Dictionary<string, object?> Sanitize(
        CollectionDefinition collection,
        IReadOnlyDictionary<string, object?> record)
    {
        var clean = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["collectionName"] = collection.Name,
        };

        foreach (var (key, value) in record)
        {
            // Mot de passe et clé de jeton ne sortent jamais, quelle que soit la route empruntée.
            if (collection.Field(key)?.Hidden == true)
            {
                continue;
            }

            clean[key] = value;
        }

        return clean;
    }

    private static List<string> AsList(object? value) => value switch
    {
        null => [],
        IEnumerable<string> items => [.. items],
        string text when text.Length > 0 => [text],
        _ => [],
    };

    // Condensat d'un mot de passe qui n'existe pas : vérifié quand le compte est introuvable, pour
    // que le temps de réponse ne trahisse pas les adresses existantes.
    private static readonly string DecoyHash = PasswordHasher.Hash("cratebase-decoy-password");
}

/// <summary>Enregistrement d'auth résolu, avec ses droits effectifs.</summary>
/// <param name="Collection">Collection d'auth.</param>
/// <param name="Id">Identifiant.</param>
/// <param name="IsSuperuser">Le compte appartient-il à la collection des superadministrateurs ?</param>
/// <param name="Permissions">Permissions effectives, rôles fusionnés.</param>
/// <param name="Fields">Champs visibles, pour <c>@request.auth.*</c>.</param>
public sealed record AuthenticatedRecord(
    string Collection,
    RecordId Id,
    bool IsSuperuser,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyDictionary<string, object?> Fields);
