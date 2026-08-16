using System.Security.Cryptography;
using System.Text;
using Cratebase.Core;
using Cratebase.Data;
using Dapper;

namespace Cratebase.Auth;

/// <summary>Resolved authentication token.</summary>
/// <param name="Collection">Auth collection that issued the token.</param>
/// <param name="RecordId">Authenticated record.</param>
/// <param name="ExpiresAt">Expiration.</param>
public sealed record ResolvedToken(string Collection, RecordId RecordId, DateTimeOffset ExpiresAt);

/// <summary>
/// Store for authentication tokens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opaque, database-backed tokens, not self-signed JWTs.</b> PocketBase issues stateless HS256
/// JWTs: faster, and irrevocable — a stolen token stays valid until it expires, whatever is done
/// about it. An opaque token costs an indexed lookup per request and makes revocation immediate:
/// real sign-out, a password change that invalidates sessions, a disabled account that is truly
/// disabled.
/// </para>
/// <para>
/// The database stores only the token's <b>SHA-256 digest</b>. A leak of the table therefore
/// yields no usable token — the same reasoning as for passwords.
/// </para>
/// </remarks>
public sealed class AuthTokenStore(IDbConnectionFactory connections, IClock clock)
{
    /// <summary>Table carrying the tokens.</summary>
    public const string TableName = "_authTokens";

    /// <summary>Validity duration of a token.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Creates the tokens table if it doesn't exist.</summary>
    public async Task EnsureTableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;
        var text = dialect.ColumnType(FieldType.Text, multiple: false);

        var statements = new[]
        {
            $"""
             CREATE TABLE IF NOT EXISTS {dialect.QuoteIdentifier(TableName)} (
               {dialect.QuoteIdentifier("hash")} {text} NOT NULL PRIMARY KEY,
               {dialect.QuoteIdentifier("collection")} {text} NOT NULL,
               {dialect.QuoteIdentifier("recordId")} {text} NOT NULL,
               {dialect.QuoteIdentifier("created")} {text} NOT NULL,
               {dialect.QuoteIdentifier("expires")} {text} NOT NULL
             )
             """,
            $"""
             CREATE INDEX IF NOT EXISTS {dialect.QuoteIdentifier("idx_authTokens_record")}
               ON {dialect.QuoteIdentifier(TableName)}
               ({dialect.QuoteIdentifier("collection")}, {dialect.QuoteIdentifier("recordId")})
             """,
        };

        foreach (var statement in statements)
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(statement, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    /// <summary>Validity duration of a file token.</summary>
    /// <remarks>
    /// Very short: it travels in a URL, hence through server logs, browser history, and the
    /// <c>Referer</c> header. Its exploitation window must be tiny.
    /// </remarks>
    public static readonly TimeSpan FileTokenLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Issues a token for a record, and returns its plaintext form.</summary>
    public async Task<string> IssueAsync(
        string collection,
        RecordId recordId,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = _clock.UtcNow;

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 INSERT INTO {dialect.QuoteIdentifier(TableName)}
                   ({dialect.QuoteIdentifier("hash")}, {dialect.QuoteIdentifier("collection")},
                    {dialect.QuoteIdentifier("recordId")}, {dialect.QuoteIdentifier("created")},
                    {dialect.QuoteIdentifier("expires")})
                 VALUES (@hash, @collection, @recordId, @created, @expires)
                 """,
                new
                {
                    hash = Fingerprint(token),
                    collection,
                    recordId = recordId.ToString(),
                    created = Timestamp.Normalize(now),
                    expires = Timestamp.Normalize(now + (lifetime ?? Lifetime)),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return token;
    }

    /// <summary>Resolves a token, or returns <see langword="null"/> if unknown or expired.</summary>
    public async Task<ResolvedToken?> ResolveAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        var row = await connection.QueryFirstOrDefaultAsync<(string Collection, string RecordId, string Expires)?>(
                new CommandDefinition(
                    $"""
                     SELECT {dialect.QuoteIdentifier("collection")} AS {dialect.QuoteIdentifier("Collection")},
                            {dialect.QuoteIdentifier("recordId")}   AS {dialect.QuoteIdentifier("RecordId")},
                            {dialect.QuoteIdentifier("expires")}    AS {dialect.QuoteIdentifier("Expires")}
                     FROM {dialect.QuoteIdentifier(TableName)}
                     WHERE {dialect.QuoteIdentifier("hash")} = @hash
                     """,
                    new { hash = Fingerprint(token) },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (row is not { } found || !RecordId.TryParse(found.RecordId, out var recordId))
        {
            return null;
        }

        var expires = Timestamp.Parse(found.Expires);

        if (expires <= _clock.UtcNow)
        {
            await RevokeAsync(token, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return new ResolvedToken(found.Collection, recordId, expires);
    }

    /// <summary>Revokes a token.</summary>
    public async Task RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {dialect.QuoteIdentifier(TableName)} " +
                $"WHERE {dialect.QuoteIdentifier("hash")} = @hash",
                new { hash = Fingerprint(token) },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes every token of a record.
    /// </summary>
    /// <remarks>
    /// Called on password change and on account deactivation. Without it, changing a password
    /// after a session theft wouldn't sign the thief out — yet that's the user's first instinct,
    /// and they'd believe the problem solved.
    /// </remarks>
    public async Task RevokeAllAsync(
        string collection,
        RecordId recordId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 DELETE FROM {dialect.QuoteIdentifier(TableName)}
                 WHERE {dialect.QuoteIdentifier("collection")} = @collection
                   AND {dialect.QuoteIdentifier("recordId")} = @recordId
                 """,
                new { collection, recordId = recordId.ToString() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Deletes expired tokens.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        return await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {dialect.QuoteIdentifier(TableName)} " +
                $"WHERE {dialect.QuoteIdentifier("expires")} <= @now",
                new { now = Timestamp.Normalize(_clock.UtcNow) },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static string Fingerprint(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
