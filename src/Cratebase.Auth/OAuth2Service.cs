using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Schema;
using Dapper;

namespace Cratebase.Auth;

/// <summary>Profil renvoyé par un fournisseur externe.</summary>
/// <param name="ProviderId">Identifiant stable chez le fournisseur.</param>
/// <param name="Email">Adresse, si le fournisseur la communique.</param>
/// <param name="Name">Nom affiché.</param>
public sealed record ExternalProfile(string ProviderId, string? Email, string? Name);

/// <summary>
/// Connexion par un fournisseur d'identité externe.
/// </summary>
/// <remarks>
/// Implémente le flot OAuth2 « code d'autorisation », côté serveur. Le client obtient un code, le
/// remet ici, et le serveur seul détient le secret : c'est ce qui évite d'exposer le secret client
/// dans le navigateur.
/// </remarks>
public sealed class OAuth2Service(
    IDbConnectionFactory connections,
    CollectionRegistry registry,
    AuthTokenStore tokens,
    AuthService auth,
    IHttpClientFactory httpClients,
    IClock clock)
{
    /// <summary>Table des liens entre comptes locaux et identités externes.</summary>
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

    /// <summary>Crée la table des liens si elle n'existe pas.</summary>
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

            // ⚠️ Unicité sur (fournisseur, identifiant chez lui) : sans elle, deux comptes locaux
            // pourraient se réclamer du même compte Google, et la connexion suivante tomberait sur
            // l'un ou l'autre au hasard.
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
    /// Authentifie par un fournisseur externe : échange le code, résout le profil, relie ou crée
    /// le compte local, et émet un jeton.
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
                $"La collection « {collection.Name} » n'est pas une collection d'authentification.");
        }

        if (!provider.Enabled || string.IsNullOrWhiteSpace(provider.ClientId))
        {
            throw new CratebaseBadRequestException(
                $"Le fournisseur « {provider.Name} » n'est pas configuré.");
        }

        var accessToken = await ExchangeCodeAsync(provider, code, codeVerifier, redirectUrl, cancellationToken)
            .ConfigureAwait(false);

        var profile = await FetchProfileAsync(provider, accessToken, cancellationToken).ConfigureAwait(false);

        var linked = await FindLinkedAsync(collection.Name, provider.Name, profile.ProviderId, cancellationToken)
            .ConfigureAwait(false);

        var recordId = linked ?? await LinkOrCreateAsync(collection, provider, profile, cancellationToken)
            .ConfigureAwait(false);

        var record = await _auth.LoadAsync(collection.Name, recordId, cancellationToken).ConfigureAwait(false)
            ?? throw new CratebaseBadRequestException("Le compte lié est introuvable.");

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
            // Le corps d'erreur du fournisseur n'est pas renvoyé tel quel : il contient parfois des
            // fragments du secret client ou de la requête.
            throw new CratebaseBadRequestException(
                $"L'échange de code auprès de « {provider.Name} » a échoué.");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)
            .ConfigureAwait(false);

        return payload.TryGetProperty("access_token", out var token) && token.GetString() is { Length: > 0 } value
            ? value
            : throw new CratebaseBadRequestException(
                $"« {provider.Name} » n'a pas renvoyé de jeton d'accès.");
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

        // GitHub refuse les requêtes sans agent utilisateur.
        request.Headers.UserAgent.ParseAdd("Cratebase");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new CratebaseBadRequestException(
                $"La lecture du profil auprès de « {provider.Name} » a échoué.");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)
            .ConfigureAwait(false);

        var id = ReadString(payload, provider.IdField);

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new CratebaseBadRequestException(
                $"« {provider.Name} » n'a pas renvoyé d'identifiant de compte.");
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

        // Deux représentations distinctes, et c'est voulu : les tables système déclarent leurs
        // dates en texte, les tables d'enregistrements les laissent typer par le dialecte.
        var recordNow = dialect.ToStorage(FieldType.AutoDate, multiple: false, _clock.UtcNow);
        var now = Timestamp.Normalize(_clock.UtcNow);

        // Rattachement par adresse : si un compte local porte déjà cette adresse, on le relie
        // plutôt que d'en créer un second.
        //
        // ⚠️ Cela n'est sûr que parce qu'on exige une adresse VÉRIFIÉE par le fournisseur. Relier
        // sur une adresse non vérifiée permettrait de prendre le contrôle d'un compte local en
        // déclarant simplement son adresse chez un fournisseur complaisant.
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
                        email = profile.Email ?? $"{provider.Name}_{profile.ProviderId}@externe.local",
                        visible = dialect.ToStorage(FieldType.Bool, false, false),
                        verified = dialect.ToStorage(FieldType.Bool, false, true),

                        // Mot de passe aléatoire jamais communiqué : le compte n'est accessible que
                        // par le fournisseur, jusqu'à ce que son propriétaire en définisse un.
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
