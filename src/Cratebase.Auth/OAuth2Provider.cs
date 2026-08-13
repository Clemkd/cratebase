namespace Cratebase.Auth;

/// <summary>
/// Fournisseur d'identité externe.
/// </summary>
/// <remarks>
/// <para>
/// Décrit en <b>données</b>, et non en code : les fournisseurs se configurent depuis la console,
/// sans redéploiement. C'est ce qui interdit d'utiliser les handlers d'authentification d'ASP.NET
/// Core, qui se déclarent au démarrage — d'où l'échange de code implémenté ici, qui suit
/// simplement le flot OAuth2 « code d'autorisation ».
/// </para>
/// </remarks>
public sealed record OAuth2Provider
{
    /// <summary>Identifiant technique : <c>google</c>, <c>facebook</c>…</summary>
    public required string Name { get; init; }

    /// <summary>Libellé affiché.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Point d'autorisation, où l'utilisateur est envoyé.</summary>
    public required string AuthorizationUrl { get; init; }

    /// <summary>Point d'échange du code contre un jeton.</summary>
    public required string TokenUrl { get; init; }

    /// <summary>Point de récupération du profil.</summary>
    public required string UserInfoUrl { get; init; }

    /// <summary>Portées demandées.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Chemin JSON de l'identifiant dans la réponse de profil.</summary>
    public string IdField { get; init; } = "id";

    /// <summary>Chemin JSON de l'adresse de courriel.</summary>
    public string EmailField { get; init; } = "email";

    /// <summary>Chemin JSON du nom affiché.</summary>
    public string NameField { get; init; } = "name";

    /// <summary>Identifiant client, fourni par le fournisseur.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Secret client. Jamais renvoyé par l'API.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>Le fournisseur est-il activé ?</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Le fournisseur exige-t-il PKCE ?
    /// </summary>
    /// <remarks>
    /// Vrai partout où c'est possible. PKCE protège l'échange de code contre l'interception, et son
    /// absence est la faiblesse classique des intégrations OAuth2 côté client public.
    /// </remarks>
    public bool UsePkce { get; init; } = true;
}

/// <summary>
/// Réglages préconfigurés des fournisseurs courants.
/// </summary>
/// <remarks>
/// Les URL de ces services changent rarement, mais les retrouver coûte du temps à chaque
/// intégration. Les préréglages ne portent aucun secret : ils décrivent le protocole, pas le compte.
/// </remarks>
public static class OAuth2Presets
{
    /// <summary>Préréglages connus, indexés par nom technique.</summary>
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

    /// <summary>Complète une configuration partielle avec le préréglage correspondant.</summary>
    public static OAuth2Provider Apply(string name, string clientId, string clientSecret, bool enabled)
    {
        if (!All.TryGetValue(name, out var preset))
        {
            throw new Core.CratebaseBadRequestException($"Fournisseur inconnu : « {name} ».");
        }

        return preset with { ClientId = clientId, ClientSecret = clientSecret, Enabled = enabled };
    }
}
