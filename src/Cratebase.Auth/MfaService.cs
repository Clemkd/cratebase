using Cratebase.Core;
using Cratebase.Data;
using Dapper;

namespace Cratebase.Auth;

/// <summary>Result of enrolling in two-factor authentication.</summary>
/// <param name="Secret">Shared secret, for manual entry if needed.</param>
/// <param name="ProvisioningUri"><c>otpauth://</c> URI to present as a QR code.</param>
public sealed record MfaEnrollment(string Secret, string ProvisioningUri);

/// <summary>
/// Two-factor authentication with one-time passwords.
/// </summary>
/// <remarks>
/// Two chained factors, as in PocketBase: the password succeeds but returns no token — it returns
/// an <c>mfaId</c>, to be presented along with the second factor's code. Until the second factor is
/// supplied, <b>no token exists</b>: that's what distinguishes real two-factor authentication from
/// a mere extra screen.
/// </remarks>
public sealed class MfaService(IDbConnectionFactory connections, AuthTokenStore tokens, IClock clock)
{
    /// <summary>Secrets table.</summary>
    public const string SecretsTable = "_mfaSecrets";

    /// <summary>Table of pending challenges.</summary>
    public const string ChallengesTable = "_mfaChallenges";

    /// <summary>Validity duration of a challenge.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    private readonly AuthTokenStore _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Creates the tables if they don't exist.</summary>
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

    /// <summary>Is two-factor authentication active on this account?</summary>
    public async Task<bool> IsEnabledAsync(
        string collection,
        RecordId recordId,
        CancellationToken cancellationToken = default)
    {
        var secret = await SecretOfAsync(collection, recordId, confirmedOnly: true, cancellationToken)
            .ConfigureAwait(false);

        return secret is not null;
    }

    /// <summary>Prepares an enrollment: produces a secret, not yet confirmed.</summary>
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

        // An unconfirmed enrollment overwrites the previous one: without this, abandoning an
        // in-progress enrollment would make it impossible to start a new one.
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
    /// Confirms an enrollment by verifying a first code.
    /// </summary>
    /// <remarks>
    /// Confirmation is not a formality: without it, a mistyped secret would enable two-factor
    /// authentication on an account for which nobody can produce the code anymore.
    /// </remarks>
    public async Task ConfirmAsync(
        string collection,
        RecordId recordId,
        string code,
        CancellationToken cancellationToken = default)
    {
        var secret = await SecretOfAsync(collection, recordId, confirmedOnly: false, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CratebaseBadRequestException("No enrollment in progress.");

        if (!Totp.Verify(code, secret, _clock.UtcNow))
        {
            throw new CratebaseBadRequestException("Incorrect code.");
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

    /// <summary>Disables two-factor authentication.</summary>
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

    /// <summary>Opens a challenge after a successful first factor.</summary>
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
    /// Completes a challenge: verifies the second factor's code and issues the token.
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
            throw new CratebaseBadRequestException("Challenge expired or unknown.");
        }

        var secret = await SecretOfAsync(found.Collection, recordId, confirmedOnly: true, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CratebaseBadRequestException("Two-factor authentication is not active on this account.");

        if (!Totp.Verify(code, secret, _clock.UtcNow))
        {
            throw new CratebaseBadRequestException("Incorrect code.");
        }

        // The challenge is consumed no matter what happens next: a replayable challenge would turn
        // a single interception into permanent access.
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
