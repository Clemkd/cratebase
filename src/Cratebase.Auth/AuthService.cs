using System.Data.Common;
using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Schema;
using Dapper;

namespace Cratebase.Auth;

/// <summary>Result of an authentication.</summary>
/// <param name="Token">
/// Token to present in <c>Authorization</c>, or <see langword="null"/> if a second factor still
/// needs to be supplied.
/// </param>
/// <param name="Record">Authenticated record, hidden fields removed.</param>
/// <param name="MfaId">
/// Identifier of the two-factor challenge, when the first factor succeeded but isn't enough.
/// <b>No token is issued in this case</b> — that's what distinguishes real two-factor
/// authentication from a cosmetic confirmation screen.
/// </param>
public sealed record AuthResult(
    string? Token,
    IReadOnlyDictionary<string, object?> Record,
    string? MfaId = null);

/// <summary>
/// Authentication by identity and password, and resolution of rights.
/// </summary>
public sealed class AuthService(
    IDbConnectionFactory connections,
    CollectionRegistry registry,
    AuthTokenStore tokens,
    MfaService mfa,
    IClock clock)
{
    /// <summary>Name of the superusers collection.</summary>
    public const string SuperusersCollection = "_superusers";

    /// <summary>Name of the roles collection.</summary>
    public const string RolesCollection = "_roles";

    /// <summary>
    /// Name of the field carrying a role's permissions.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from <see cref="SystemFields.Permissions"/>: the latter is a reserved
    /// name, which the engine strips from collections where it doesn't apply. Reusing the same
    /// name would make the roles' field vanish at the first normalization, without an error.
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
    /// Authenticates an account.
    /// </summary>
    /// <remarks>
    /// The failure message is <b>the same</b> whether the address is unknown or the password is
    /// wrong, and a decoy digest is verified in the first case: without that, the difference in
    /// response time would tell an attacker which addresses exist.
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
                $"Collection \"{collection.Name}\" is not an authentication collection.");
        }

        var record = await FindByEmailAsync(collection, identity, cancellationToken).ConfigureAwait(false);
        var hash = record?.GetValueOrDefault(SystemFields.Password) as string;

        if (!PasswordHasher.Verify(password, hash ?? DecoyHash))
        {
            throw new CratebaseBadRequestException("Incorrect identity or password.");
        }

        if (record is null || !RecordId.TryParse(record.GetValueOrDefault(SystemFields.Id) as string, out var id))
        {
            throw new CratebaseBadRequestException("Incorrect identity or password.");
        }

        // The password is correct, but it isn't enough: return a challenge, not a token.
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

    /// <summary>Loads an auth record and its effective permissions.</summary>
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
    /// Computes effective permissions: those from roles, plus individual overrides.
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

        // Placeholders generated one by one, rather than relying on Dapper's automatic expansion
        // of "IN @names": it's driver-dependent, and where it doesn't apply, the array is sent as
        // a single parameter and the engine rejects the query. Explicit, therefore portable.
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
    /// Creates or updates a superuser.
    /// </summary>
    /// <remarks>
    /// Used at initial startup (environment variables) and from the CLI. Deliberately outside the
    /// records API: a superuser account is not created by a POST.
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
                    [$"The password must be at least {PasswordHasher.MinimumLength} characters long."],
            });
        }

        var collection = _registry.Require(SuperusersCollection);
        var existing = await FindByEmailAsync(collection, email, cancellationToken).ConfigureAwait(false);

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        // ⚠️ Go through ToStorage, not the canonical string directly. Date columns of a records
        // table are typed by the dialect: "TEXT" on SQLite, but "timestamptz" on PostgreSQL, which
        // flatly refuses a string. Hardcoding the date therefore works until migration day, and no
        // further.
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

            // Changing the password signs open sessions out. This is the expected reflex after a
            // session theft, and it would have no effect without this line.
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
                    // Always through the dialect: on PostgreSQL, these columns are jsonb and
                    // flatly refuse a bare string.
                    roles = dialect.ToStorage(FieldType.Text, multiple: true, null),
                    permissions = dialect.ToStorage(FieldType.Text, multiple: true, null),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Is there at least one superuser?</summary>
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

    /// <summary>
    /// Identifier of the sole superuser, or <see langword="null"/> if there are zero or several.
    /// </summary>
    /// <remarks>
    /// Returns the identifier rather than a bare count: the guard must refuse deleting the
    /// <b>last</b> account, not just any one while one remains. Without this distinction, deleting
    /// a nonexistent identifier would produce a conflict where a 404 is due.
    /// </remarks>
    public async Task<string?> OnlySuperuserIdAsync(CancellationToken cancellationToken = default)
    {
        if (_registry.Find(SuperusersCollection) is not { } collection)
        {
            return null;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        // Two rows are enough to decide: beyond that, an exact count adds nothing.
        var identifiers = await connection.QueryAsync<string>(new CommandDefinition(
                $"SELECT {dialect.QuoteIdentifier(SystemFields.Id)} " +
                $"FROM {dialect.QuoteIdentifier(collection.TableName)} " +
                dialect.LimitOffset(2, 0),
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var found = identifiers.ToList();

        return found.Count == 1 ? found[0] : null;
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
            // Password and token key never leave, regardless of the route taken.
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

    // Digest of a password that doesn't exist: verified when the account isn't found, so response
    // time doesn't betray which addresses exist.
    private static readonly string DecoyHash = PasswordHasher.Hash("cratebase-decoy-password");
}

/// <summary>Resolved auth record, with its effective rights.</summary>
/// <param name="Collection">Auth collection.</param>
/// <param name="Id">Identifier.</param>
/// <param name="IsSuperuser">Does the account belong to the superusers collection?</param>
/// <param name="Permissions">Effective permissions, roles merged in.</param>
/// <param name="Fields">Visible fields, for <c>@request.auth.*</c>.</param>
public sealed record AuthenticatedRecord(
    string Collection,
    RecordId Id,
    bool IsSuperuser,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyDictionary<string, object?> Fields);
