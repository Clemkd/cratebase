using System.Security.Cryptography;
using System.Text;
using Cratebase.Core;
using Cratebase.Data;
using Dapper;

namespace Cratebase.Auth;

/// <summary>Jeton d'authentification résolu.</summary>
/// <param name="Collection">Collection d'auth ayant émis le jeton.</param>
/// <param name="RecordId">Enregistrement authentifié.</param>
/// <param name="ExpiresAt">Expiration.</param>
public sealed record ResolvedToken(string Collection, RecordId RecordId, DateTimeOffset ExpiresAt);

/// <summary>
/// Magasin des jetons d'authentification.
/// </summary>
/// <remarks>
/// <para>
/// <b>Jetons opaques stockés en base, et non JWT auto-signés.</b> PocketBase émet des JWT HS256
/// sans état : c'est plus rapide, et c'est irrévocable — un jeton volé reste valable jusqu'à son
/// expiration, quoi qu'on fasse. Un jeton opaque coûte une lecture indexée par requête et rend la
/// révocation immédiate : déconnexion réelle, changement de mot de passe qui invalide les sessions,
/// compte désactivé qui l'est vraiment.
/// </para>
/// <para>
/// La base ne stocke que le <b>condensat SHA-256</b> du jeton. Une fuite de la table ne donne donc
/// aucun jeton utilisable — même raisonnement que pour les mots de passe.
/// </para>
/// </remarks>
public sealed class AuthTokenStore(IDbConnectionFactory connections, IClock clock)
{
    /// <summary>Table portant les jetons.</summary>
    public const string TableName = "_authTokens";

    /// <summary>Durée de validité d'un jeton.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Crée la table des jetons si elle n'existe pas.</summary>
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

    /// <summary>Durée de validité d'un jeton de fichier.</summary>
    /// <remarks>
    /// Très courte : il circule dans une URL, donc dans les journaux de serveur, l'historique du
    /// navigateur et l'en-tête <c>Referer</c>. Sa fenêtre d'exploitation doit être minuscule.
    /// </remarks>
    public static readonly TimeSpan FileTokenLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Émet un jeton pour un enregistrement, et renvoie sa forme en clair.</summary>
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

    /// <summary>Résout un jeton, ou rend <see langword="null"/> s'il est inconnu ou expiré.</summary>
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

    /// <summary>Révoque un jeton.</summary>
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
    /// Révoque tous les jetons d'un enregistrement.
    /// </summary>
    /// <remarks>
    /// Appelé au changement de mot de passe et à la désactivation d'un compte. Sans cela, changer
    /// son mot de passe après un vol de session ne déconnecte pas le voleur — c'est pourtant le
    /// premier réflexe de l'utilisateur, et il croirait le problème réglé.
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

    /// <summary>Supprime les jetons expirés.</summary>
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
