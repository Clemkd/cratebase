using Cratebase.Core;
using Cratebase.Data;
using Dapper;

namespace Cratebase.Auth;

/// <summary>Résultat d'une inscription à la double authentification.</summary>
/// <param name="Secret">Secret partagé, à saisir manuellement au besoin.</param>
/// <param name="ProvisioningUri">URI <c>otpauth://</c> à présenter en code QR.</param>
public sealed record MfaEnrollment(string Secret, string ProvisioningUri);

/// <summary>
/// Double authentification par mot de passe à usage unique.
/// </summary>
/// <remarks>
/// Deux facteurs enchaînés, comme chez PocketBase : le mot de passe réussit mais ne rend pas de
/// jeton — il rend un <c>mfaId</c>, à présenter avec le code du second facteur. Tant que le second
/// facteur n'est pas fourni, <b>aucun jeton n'existe</b> : c'est ce qui distingue une vraie double
/// authentification d'un simple écran supplémentaire.
/// </remarks>
public sealed class MfaService(IDbConnectionFactory connections, AuthTokenStore tokens, IClock clock)
{
    /// <summary>Table des secrets.</summary>
    public const string SecretsTable = "_mfaSecrets";

    /// <summary>Table des défis en cours.</summary>
    public const string ChallengesTable = "_mfaChallenges";

    /// <summary>Durée de validité d'un défi.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    private readonly AuthTokenStore _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Crée les tables si elles n'existent pas.</summary>
    public async Task EnsureTablesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;
        var text = dialect.ColumnType(FieldType.Text, multiple: false);
        var flag = dialect.ColumnType(FieldType.Bool, multiple: false);

        var statements = new[]
        {
            $"""
             CREATE TABLE IF NOT EXISTS {dialect.QuoteIdentifier(SecretsTable)} (
               {dialect.QuoteIdentifier("id")} {text} NOT NULL PRIMARY KEY,
               {dialect.QuoteIdentifier("collection")} {text} NOT NULL,
               {dialect.QuoteIdentifier("recordId")} {text} NOT NULL,
               {dialect.QuoteIdentifier("secret")} {text} NOT NULL,
               {dialect.QuoteIdentifier("confirmed")} {flag} NOT NULL,
               {dialect.QuoteIdentifier("created")} {text} NOT NULL
             )
             """,
            $"""
             CREATE UNIQUE INDEX IF NOT EXISTS {dialect.QuoteIdentifier("idx_mfaSecrets_record")}
               ON {dialect.QuoteIdentifier(SecretsTable)}
               ({dialect.QuoteIdentifier("collection")}, {dialect.QuoteIdentifier("recordId")})
             """,
            $"""
             CREATE TABLE IF NOT EXISTS {dialect.QuoteIdentifier(ChallengesTable)} (
               {dialect.QuoteIdentifier("id")} {text} NOT NULL PRIMARY KEY,
               {dialect.QuoteIdentifier("collection")} {text} NOT NULL,
               {dialect.QuoteIdentifier("recordId")} {text} NOT NULL,
               {dialect.QuoteIdentifier("expires")} {text} NOT NULL
             )
             """,
        };

        foreach (var statement in statements)
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(statement, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    /// <summary>La double authentification est-elle active sur ce compte ?</summary>
    public async Task<bool> IsEnabledAsync(
        string collection,
        RecordId recordId,
        CancellationToken cancellationToken = default)
    {
        var secret = await SecretOfAsync(collection, recordId, confirmedOnly: true, cancellationToken)
            .ConfigureAwait(false);

        return secret is not null;
    }

    /// <summary>Prépare une inscription : produit un secret, non encore confirmé.</summary>
    public async Task<MfaEnrollment> EnrollAsync(
        string collection,
        RecordId recordId,
        string account,
        string issuer,
        CancellationToken cancellationToken = default)
    {
        var secret = Totp.NewSecret();

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        // Une inscription non confirmée écrase la précédente : sans cela, abandonner une
        // inscription en cours empêcherait d'en relancer une.
        await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 DELETE FROM {dialect.QuoteIdentifier(SecretsTable)}
                 WHERE {dialect.QuoteIdentifier("collection")} = @collection
                   AND {dialect.QuoteIdentifier("recordId")} = @recordId
                   AND {dialect.QuoteIdentifier("confirmed")} = @unconfirmed
                 """,
                new
                {
                    collection,
                    recordId = recordId.ToString(),
                    unconfirmed = dialect.ToStorage(FieldType.Bool, false, false),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 INSERT INTO {dialect.QuoteIdentifier(SecretsTable)}
                   ({dialect.QuoteIdentifier("id")}, {dialect.QuoteIdentifier("collection")},
                    {dialect.QuoteIdentifier("recordId")}, {dialect.QuoteIdentifier("secret")},
                    {dialect.QuoteIdentifier("confirmed")}, {dialect.QuoteIdentifier("created")})
                 VALUES (@id, @collection, @recordId, @secret, @confirmed, @now)
                 """,
                new
                {
                    id = RecordId.New().ToString(),
                    collection,
                    recordId = recordId.ToString(),
                    secret,
                    confirmed = dialect.ToStorage(FieldType.Bool, false, false),
                    now = Timestamp.Normalize(_clock.UtcNow),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return new MfaEnrollment(secret, Totp.ProvisioningUri(secret, account, issuer));
    }

    /// <summary>
    /// Confirme une inscription en vérifiant un premier code.
    /// </summary>
    /// <remarks>
    /// La confirmation n'est pas une formalité : sans elle, un secret mal recopié activerait la
    /// double authentification sur un compte dont plus personne ne peut produire le code.
    /// </remarks>
    public async Task ConfirmAsync(
        string collection,
        RecordId recordId,
        string code,
        CancellationToken cancellationToken = default)
    {
        var secret = await SecretOfAsync(collection, recordId, confirmedOnly: false, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CratebaseBadRequestException("Aucune inscription en cours.");

        if (!Totp.Verify(code, secret, _clock.UtcNow))
        {
            throw new CratebaseBadRequestException("Code incorrect.");
        }

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 UPDATE {dialect.QuoteIdentifier(SecretsTable)}
                 SET {dialect.QuoteIdentifier("confirmed")} = @confirmed
                 WHERE {dialect.QuoteIdentifier("collection")} = @collection
                   AND {dialect.QuoteIdentifier("recordId")} = @recordId
                 """,
                new
                {
                    confirmed = dialect.ToStorage(FieldType.Bool, false, true),
                    collection,
                    recordId = recordId.ToString(),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Désactive la double authentification.</summary>
    public async Task DisableAsync(
        string collection,
        RecordId recordId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 DELETE FROM {dialect.QuoteIdentifier(SecretsTable)}
                 WHERE {dialect.QuoteIdentifier("collection")} = @collection
                   AND {dialect.QuoteIdentifier("recordId")} = @recordId
                 """,
                new { collection, recordId = recordId.ToString() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Ouvre un défi après un premier facteur réussi.</summary>
    public async Task<string> ChallengeAsync(
        string collection,
        RecordId recordId,
        CancellationToken cancellationToken = default)
    {
        var id = RecordId.New().ToString();

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 INSERT INTO {dialect.QuoteIdentifier(ChallengesTable)}
                   ({dialect.QuoteIdentifier("id")}, {dialect.QuoteIdentifier("collection")},
                    {dialect.QuoteIdentifier("recordId")}, {dialect.QuoteIdentifier("expires")})
                 VALUES (@id, @collection, @recordId, @expires)
                 """,
                new
                {
                    id,
                    collection,
                    recordId = recordId.ToString(),
                    expires = Timestamp.Normalize(_clock.UtcNow + ChallengeLifetime),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return id;
    }

    /// <summary>
    /// Achève un défi : vérifie le code du second facteur et émet le jeton.
    /// </summary>
    public async Task<(string Token, string Collection, RecordId RecordId)> CompleteAsync(
        string challengeId,
        string code,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        var challenge = await connection
            .QueryFirstOrDefaultAsync<(string Collection, string RecordId, string Expires)?>(
                new CommandDefinition(
                    $"""
                     SELECT {dialect.QuoteIdentifier("collection")} AS {dialect.QuoteIdentifier("Collection")},
                            {dialect.QuoteIdentifier("recordId")}   AS {dialect.QuoteIdentifier("RecordId")},
                            {dialect.QuoteIdentifier("expires")}    AS {dialect.QuoteIdentifier("Expires")}
                     FROM {dialect.QuoteIdentifier(ChallengesTable)}
                     WHERE {dialect.QuoteIdentifier("id")} = @id
                     """,
                    new { id = challengeId },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (challenge is not { } found
            || !RecordId.TryParse(found.RecordId, out var recordId)
            || Timestamp.Parse(found.Expires) <= _clock.UtcNow)
        {
            throw new CratebaseBadRequestException("Défi expiré ou inconnu.");
        }

        var secret = await SecretOfAsync(found.Collection, recordId, confirmedOnly: true, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CratebaseBadRequestException("Double authentification inactive sur ce compte.");

        if (!Totp.Verify(code, secret, _clock.UtcNow))
        {
            throw new CratebaseBadRequestException("Code incorrect.");
        }

        // Le défi est consommé, quoi qu'il arrive ensuite : un défi rejouable transformerait une
        // interception unique en accès permanent.
        await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {dialect.QuoteIdentifier(ChallengesTable)} " +
                $"WHERE {dialect.QuoteIdentifier("id")} = @id",
                new { id = challengeId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var token = await _tokens
            .IssueAsync(found.Collection, recordId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return (token, found.Collection, recordId);
    }

    private async Task<string?> SecretOfAsync(
        string collection,
        RecordId recordId,
        bool confirmedOnly,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        var clause = confirmedOnly
            ? $" AND {dialect.QuoteIdentifier("confirmed")} = @confirmed"
            : string.Empty;

        return await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
                $"""
                 SELECT {dialect.QuoteIdentifier("secret")}
                 FROM {dialect.QuoteIdentifier(SecretsTable)}
                 WHERE {dialect.QuoteIdentifier("collection")} = @collection
                   AND {dialect.QuoteIdentifier("recordId")} = @recordId{clause}
                 """,
                new
                {
                    collection,
                    recordId = recordId.ToString(),
                    confirmed = dialect.ToStorage(FieldType.Bool, false, true),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
