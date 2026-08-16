using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Schema;
using Dapper;

namespace Cratebase.Auth;

/// <summary>Profile returned by an external provider.</summary>
/// <param name="ProviderId">Stable identifier at the provider.</param>
/// <param name="Email">Address, if the provider supplies it.</param>
/// <param name="Name">Display name.</param>
public sealed record ExternalProfile(string ProviderId, string? Email, string? Name);

/// <summary>
/// Sign-in through an external identity provider.
/// </summary>
/// <remarks>
/// Implements the server-side OAuth2 "authorization code" flow. The client obtains a code, hands
/// it here, and only the server holds the secret: that's what avoids exposing the client secret in
/// the browser.
/// </remarks>
public sealed class OAuth2Service(
    IDbConnectionFactory connections,
    CollectionRegistry registry,
    AuthTokenStore tokens,
    AuthService auth,
    IHttpClientFactory httpClients,
    IClock clock)
{
    /// <summary>Table of links between local accounts and external identities.</summary>
    public const string LinksTable = "_externalAuths";

    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    private readonly CollectionRegistry _registry = registry
        ?? throw new ArgumentNullException(nameof(registry));

    private readonly AuthTokenStore _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private readonly AuthService _auth = auth ?? throw new ArgumentNullException(nameof(auth));

    private readonly IHttpClientFactory _httpClients = httpClients
        ?? throw new ArgumentNullException(nameof(httpClients));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Creates the links table if it doesn't exist.</summary>
    public async Task EnsureTableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;
        var text = dialect.ColumnType(FieldType.Text, multiple: false);

        var statements = new[]
        {
            $"""
             CREATE TABLE IF NOT EXISTS {dialect.QuoteIdentifier(LinksTable)} (
               {dialect.QuoteIdentifier("id")} {text} NOT NULL PRIMARY KEY,
               {dialect.QuoteIdentifier("collection")} {text} NOT NULL,
               {dialect.QuoteIdentifier("recordId")} {text} NOT NULL,
               {dialect.QuoteIdentifier("provider")} {text} NOT NULL,
               {dialect.QuoteIdentifier("providerId")} {text} NOT NULL,
               {dialect.QuoteIdentifier("created")} {text} NOT NULL
             )
             """,

            // ⚠️ Uniqueness on (provider, id at the provider): without it, two local accounts could
            // both claim the same Google account, and the next sign-in would land on either one at
            // random.
            $"""
             CREATE UNIQUE INDEX IF NOT EXISTS {dialect.QuoteIdentifier("idx_externalAuths_identity")}
               ON {dialect.QuoteIdentifier(LinksTable)}
               ({dialect.QuoteIdentifier("provider")}, {dialect.QuoteIdentifier("providerId")})
             """,
        };

        foreach (var statement in statements)
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(statement, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Authenticates through an external provider: exchanges the code, resolves the profile, links
    /// or creates the local account, and issues a token.
    /// </summary>
    public async Task<AuthResult> AuthenticateAsync(
        string collectionName,
        OAuth2Provider provider,
        string code,
        string? codeVerifier,
        string redirectUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var collection = _registry.Require(collectionName);

        if (collection.Kind is not CollectionKind.Auth)
        {
            throw new CratebaseBadRequestException(
                $"Collection \"{collection.Name}\" is not an authentication collection.");
        }

        if (!provider.Enabled || string.IsNullOrWhiteSpace(provider.ClientId))
        {
            throw new CratebaseBadRequestException(
                $"Provider \"{provider.Name}\" is not configured.");
        }

        var accessToken = await ExchangeCodeAsync(provider, code, codeVerifier, redirectUrl, cancellationToken)
            .ConfigureAwait(false);

        var profile = await FetchProfileAsync(provider, accessToken, cancellationToken).ConfigureAwait(false);

        var linked = await FindLinkedAsync(collection.Name, provider.Name, profile.ProviderId, cancellationToken)
            .ConfigureAwait(false);

        var recordId = linked ?? await LinkOrCreateAsync(collection, provider, profile, cancellationToken)
            .ConfigureAwait(false);

        var record = await _auth.LoadAsync(collection.Name, recordId, cancellationToken).ConfigureAwait(false)
            ?? throw new CratebaseBadRequestException("The linked account could not be found.");

        var token = await _tokens
            .IssueAsync(collection.Name, recordId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new AuthResult(token, record.Fields);
    }

    private async Task<string> ExchangeCodeAsync(
        OAuth2Provider provider,
        string code,
        string? codeVerifier,
        string redirectUrl,
        CancellationToken cancellationToken)
    {
        var client = _httpClients.CreateClient("cratebase-oauth2");

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUrl,
            ["client_id"] = provider.ClientId,
            ["client_secret"] = provider.ClientSecret,
        };

        if (provider.UsePkce && !string.IsNullOrWhiteSpace(codeVerifier))
        {
            form["code_verifier"] = codeVerifier;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, provider.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The provider's error body is not returned as-is: it sometimes contains fragments of
            // the client secret or of the request.
            throw new CratebaseBadRequestException(
                $"The code exchange with \"{provider.Name}\" failed.");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)
            .ConfigureAwait(false);

        return payload.TryGetProperty("access_token", out var token) && token.GetString() is { Length: > 0 } value
            ? value
            : throw new CratebaseBadRequestException(
                $"\"{provider.Name}\" did not return an access token.");
    }

    private async Task<ExternalProfile> FetchProfileAsync(
        OAuth2Provider provider,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var client = _httpClients.CreateClient("cratebase-oauth2");

        using var request = new HttpRequestMessage(HttpMethod.Get, provider.UserInfoUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // GitHub refuses requests with no user agent.
        request.Headers.UserAgent.ParseAdd("Cratebase");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new CratebaseBadRequestException(
                $"Reading the profile from \"{provider.Name}\" failed.");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)
            .ConfigureAwait(false);

        var id = ReadString(payload, provider.IdField);

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new CratebaseBadRequestException(
                $"\"{provider.Name}\" did not return an account identifier.");
        }

        return new ExternalProfile(
            id,
            ReadString(payload, provider.EmailField),
            ReadString(payload, provider.NameField));
    }

    private async Task<RecordId?> FindLinkedAsync(
        string collection,
        string provider,
        string providerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        var found = await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
                $"""
                 SELECT {dialect.QuoteIdentifier("recordId")}
                 FROM {dialect.QuoteIdentifier(LinksTable)}
                 WHERE {dialect.QuoteIdentifier("collection")} = @collection
                   AND {dialect.QuoteIdentifier("provider")} = @provider
                   AND {dialect.QuoteIdentifier("providerId")} = @providerId
                 """,
                new { collection, provider, providerId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return RecordId.TryParse(found, out var id) ? id : null;
    }

    private async Task<RecordId> LinkOrCreateAsync(
        CollectionDefinition collection,
        OAuth2Provider provider,
        ExternalProfile profile,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        // Two distinct representations, and that's intended: system tables declare their dates as
        // text, records tables let the dialect type them.
        var recordNow = dialect.ToStorage(FieldType.AutoDate, multiple: false, _clock.UtcNow);
        var now = Timestamp.Normalize(_clock.UtcNow);

        // Linking by address: if a local account already carries this address, link to it rather
        // than create a second one.
        //
        // ⚠️ This is only safe because a VERIFIED address from the provider is required. Linking
        // on an unverified address would let an attacker take over a local account simply by
        // claiming its address with a complicit provider.
        RecordId recordId;

        var existing = string.IsNullOrWhiteSpace(profile.Email)
            ? null
            : await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
                    $"SELECT {dialect.QuoteIdentifier(SystemFields.Id)} " +
                    $"FROM {dialect.QuoteIdentifier(collection.TableName)} " +
                    $"WHERE {dialect.QuoteIdentifier(SystemFields.Email)} = @email",
                    new { email = profile.Email },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

        if (RecordId.TryParse(existing, out var found))
        {
            recordId = found;
        }
        else
        {
            recordId = RecordId.New();

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
                        id = recordId.ToString(),
                        now = recordNow,
                        email = profile.Email ?? $"{provider.Name}_{profile.ProviderId}@external.local",
                        visible = dialect.ToStorage(FieldType.Bool, false, false),
                        verified = dialect.ToStorage(FieldType.Bool, false, true),

                        // Random password, never communicated: the account is reachable only
                        // through the provider, until its owner sets one.
                        password = PasswordHasher.Hash(Guid.CreateVersion7().ToString()),
                        tokenKey = RecordId.New().ToString(),
                        roles = dialect.ToStorage(FieldType.Text, multiple: true, null),
                        permissions = dialect.ToStorage(FieldType.Text, multiple: true, null),
                    },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 INSERT INTO {dialect.QuoteIdentifier(LinksTable)}
                   ({dialect.QuoteIdentifier("id")}, {dialect.QuoteIdentifier("collection")},
                    {dialect.QuoteIdentifier("recordId")}, {dialect.QuoteIdentifier("provider")},
                    {dialect.QuoteIdentifier("providerId")}, {dialect.QuoteIdentifier("created")})
                 VALUES (@id, @collection, @recordId, @provider, @providerId, @now)
                 """,
                new
                {
                    id = RecordId.New().ToString(),
                    collection = collection.Name,
                    recordId = recordId.ToString(),
                    provider = provider.Name,
                    providerId = profile.ProviderId,
                    now,
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return recordId;
    }

    private static string? ReadString(JsonElement payload, string path)
    {
        var current = payload;

        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind is not JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.ToString(),
            _ => null,
        };
    }
}
