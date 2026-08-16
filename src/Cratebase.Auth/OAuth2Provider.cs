namespace Cratebase.Auth;

/// <summary>
/// External identity provider.
/// </summary>
/// <remarks>
/// <para>
/// Described as <b>data</b>, not code: providers are configured from the console, without a
/// redeploy. That's what rules out using ASP.NET Core's own authentication handlers, which are
/// declared at startup — hence the code exchange implemented here, which simply follows the
/// OAuth2 "authorization code" flow.
/// </para>
/// </remarks>
public sealed record OAuth2Provider
{
    /// <summary>Technical identifier: <c>google</c>, <c>facebook</c>…</summary>
    public required string Name { get; init; }

    /// <summary>Displayed label.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Authorization endpoint, where the user is sent.</summary>
    public required string AuthorizationUrl { get; init; }

    /// <summary>Endpoint for exchanging the code for a token.</summary>
    public required string TokenUrl { get; init; }

    /// <summary>Endpoint for fetching the profile.</summary>
    public required string UserInfoUrl { get; init; }

    /// <summary>Requested scopes.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>JSON path of the identifier in the profile response.</summary>
    public string IdField { get; init; } = "id";

    /// <summary>JSON path of the email address.</summary>
    public string EmailField { get; init; } = "email";

    /// <summary>JSON path of the display name.</summary>
    public string NameField { get; init; } = "name";

    /// <summary>Client identifier, supplied by the provider.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Client secret. Never returned by the API.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>Is the provider enabled?</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Does the provider require PKCE?
    /// </summary>
    /// <remarks>
    /// True everywhere it's possible. PKCE protects the code exchange against interception, and
    /// its absence is the classic weakness of public-client OAuth2 integrations.
    /// </remarks>
    public bool UsePkce { get; init; } = true;
}

/// <summary>
/// Preconfigured settings for common providers.
/// </summary>
/// <remarks>
/// These services' URLs rarely change, but looking them up costs time on every integration. The
/// presets carry no secret: they describe the protocol, not the account.
/// </remarks>
public static class OAuth2Presets
{
    /// <summary>Known presets, indexed by technical name.</summary>
    public static IReadOnlyDictionary<string, OAuth2Provider> All { get; } =
        new Dictionary<string, OAuth2Provider>(StringComparer.OrdinalIgnoreCase)
        {
            ["google"] = new OAuth2Provider
            {
                Name = "google",
                DisplayName = "Google",
                AuthorizationUrl = "https://accounts.google.com/o/oauth2/v2/auth",
                TokenUrl = "https://oauth2.googleapis.com/token",
                UserInfoUrl = "https://openidconnect.googleapis.com/v1/userinfo",
                Scopes = ["openid", "email", "profile"],
                IdField = "sub",
            },

            ["facebook"] = new OAuth2Provider
            {
                Name = "facebook",
                DisplayName = "Facebook",
                AuthorizationUrl = "https://www.facebook.com/v21.0/dialog/oauth",
                TokenUrl = "https://graph.facebook.com/v21.0/oauth/access_token",
                UserInfoUrl = "https://graph.facebook.com/me?fields=id,name,email",
                Scopes = ["email", "public_profile"],
            },

            ["microsoft"] = new OAuth2Provider
            {
                Name = "microsoft",
                DisplayName = "Microsoft",
                AuthorizationUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
                TokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                UserInfoUrl = "https://graph.microsoft.com/oidc/userinfo",
                Scopes = ["openid", "email", "profile"],
                IdField = "sub",
            },

            ["github"] = new OAuth2Provider
            {
                Name = "github",
                DisplayName = "GitHub",
                AuthorizationUrl = "https://github.com/login/oauth/authorize",
                TokenUrl = "https://github.com/login/oauth/access_token",
                UserInfoUrl = "https://api.github.com/user",
                Scopes = ["read:user", "user:email"],
                IdField = "id",
                NameField = "name",
            },
        };

    /// <summary>Fills in a partial configuration with the matching preset.</summary>
    public static OAuth2Provider Apply(string name, string clientId, string clientSecret, bool enabled)
    {
        if (!All.TryGetValue(name, out var preset))
        {
            throw new Core.CratebaseBadRequestException($"Unknown provider: \"{name}\".");
        }

        return preset with { ClientId = clientId, ClientSecret = clientSecret, Enabled = enabled };
    }
}
