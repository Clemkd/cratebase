using Cratebase.Auth;
using Cratebase.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cratebase.Server;

/// <summary>Corps d'une demande d'authentification.</summary>
/// <param name="Identity">Adresse de courriel.</param>
/// <param name="Password">Mot de passe.</param>
public sealed record AuthWithPasswordRequest(string Identity, string Password);

/// <summary>Corps d'une attribution de droits.</summary>
/// <param name="Roles">Rôles à poser.</param>
/// <param name="Permissions">Dérogations individuelles à poser.</param>
public sealed record GrantRequest(IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

/// <summary>Corps d'un second facteur.</summary>
/// <param name="MfaId">Identifiant du défi rendu par le premier facteur.</param>
/// <param name="Code">Code à usage unique.</param>
public sealed record AuthWithOtpRequest(string MfaId, string Code);

/// <summary>Corps d'un code de double authentification.</summary>
/// <param name="Code">Code à usage unique.</param>
public sealed record MfaCodeRequest(string Code);

/// <summary>Corps d'une connexion par fournisseur externe.</summary>
/// <param name="Provider">Nom technique du fournisseur.</param>
/// <param name="Code">Code d'autorisation reçu du fournisseur.</param>
/// <param name="CodeVerifier">Vérificateur PKCE, si le flot en utilise un.</param>
/// <param name="RedirectUrl">URL de redirection, qui doit correspondre à celle de l'autorisation.</param>
public sealed record AuthWithOAuth2Request(
    string Provider,
    string Code,
    string? CodeVerifier,
    string RedirectUrl);

/// <summary>
/// Endpoints d'authentification.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Publie les routes d'authentification.</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/collections/{collection}");

        group.MapPost("/auth-with-password", async (
            string collection,
            AuthWithPasswordRequest request,
            AuthService auth,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(request);

            var result = await auth
                .AuthenticateAsync(collection, request.Identity, request.Password, cancellationToken)
                .ConfigureAwait(false);

            // Second facteur exigé : 401 et un identifiant de défi, sémantique de PocketBase. Le
            // 401 est important — un 200 laisserait croire à un client naïf que la session est
            // ouverte.
            return result.MfaId is { } challenge
                ? Results.Json(new { mfaId = challenge }, statusCode: 401)
                : Results.Ok(new { token = result.Token, record = result.Record });
        });

        group.MapPost("/auth-with-otp", async (
            string collection,
            AuthWithOtpRequest request,
            MfaService mfa,
            AuthService auth,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(request);

            var (token, resolvedCollection, recordId) = await mfa
                .CompleteAsync(request.MfaId, request.Code, cancellationToken)
                .ConfigureAwait(false);

            var record = await auth.LoadAsync(resolvedCollection, recordId, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { token, record = record?.Fields });
        });

        group.MapGet("/auth-methods", (
            string collection,
            CratebaseOptions options) => Results.Ok(new
            {
                password = true,
                oauth2 = options.OAuth2Providers.Values
                    .Where(provider => provider.Enabled)
                    // ⚠️ Ni le secret client, ni rien qui en dérive. Cet endpoint est public par
                    // nature : l'écran de connexion doit savoir quels boutons afficher.
                    .Select(provider => new
                    {
                        name = provider.Name,
                        displayName = provider.DisplayName,
                        authorizationUrl = provider.AuthorizationUrl,
                        clientId = provider.ClientId,
                        scopes = provider.Scopes,
                        usePkce = provider.UsePkce,
                    }),
            }));

        group.MapPost("/auth-with-oauth2", async (
            string collection,
            AuthWithOAuth2Request request,
            OAuth2Service oauth2,
            CratebaseOptions options,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!options.OAuth2Providers.TryGetValue(request.Provider, out var provider))
            {
                throw new CratebaseBadRequestException(
                    $"Le fournisseur « {request.Provider} » n'est pas configuré.");
            }

            var result = await oauth2
                .AuthenticateAsync(
                    collection, provider, request.Code, request.CodeVerifier,
                    request.RedirectUrl, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { token = result.Token, record = result.Record });
        });

        group.MapPost("/mfa/enroll", async (
            string collection,
            MfaService mfa,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (!user.IsAuthenticated || user.Id is not { } id)
            {
                throw new CratebaseUnauthenticatedException();
            }

            var account = user.Fields.GetValueOrDefault("email") as string ?? id.ToString();

            var enrollment = await mfa
                .EnrollAsync(collection, id, account, "Cratebase", cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { secret = enrollment.Secret, uri = enrollment.ProvisioningUri });
        });

        group.MapPost("/mfa/confirm", async (
            string collection,
            MfaCodeRequest request,
            MfaService mfa,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!user.IsAuthenticated || user.Id is not { } id)
            {
                throw new CratebaseUnauthenticatedException();
            }

            await mfa.ConfirmAsync(collection, id, request.Code, cancellationToken).ConfigureAwait(false);

            return Results.NoContent();
        });

        group.MapPost("/mfa/disable", async (
            string collection,
            MfaCodeRequest request,
            MfaService mfa,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!user.IsAuthenticated || user.Id is not { } id)
            {
                throw new CratebaseUnauthenticatedException();
            }

            // Un code valide est exigé pour désactiver : sans cela, un jeton volé suffirait à
            // retirer le second facteur, ce qui annule tout l'intérêt du dispositif.
            await mfa.ConfirmAsync(collection, id, request.Code, cancellationToken).ConfigureAwait(false);
            await mfa.DisableAsync(collection, id, cancellationToken).ConfigureAwait(false);

            return Results.NoContent();
        });

        group.MapPost("/auth-refresh", async (
            string collection,
            HttpContext http,
            AuthService auth,
            AuthTokenStore tokens,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (!user.IsAuthenticated || user.Id is not { } id)
            {
                throw new CratebaseUnauthenticatedException();
            }

            var record = await auth.LoadAsync(collection, id, cancellationToken).ConfigureAwait(false)
                ?? throw new CratebaseUnauthenticatedException();

            // Rotation : l'ancien jeton est révoqué, un nouveau est émis. Un jeton renouvelable à
            // l'infini sans rotation ne se distingue pas d'un jeton permanent.
            var previous = BearerToken(http);

            if (previous is not null)
            {
                await tokens.RevokeAsync(previous, cancellationToken).ConfigureAwait(false);
            }

            var token = await tokens
                .IssueAsync(collection, id, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { token, record = record.Fields });
        });

        group.MapPost("/auth-logout", async (
            HttpContext http,
            AuthTokenStore tokens,
            CancellationToken cancellationToken) =>
        {
            // Déconnexion réelle : le jeton disparaît de la base. C'est ce que les jetons opaques
            // achètent, et qu'un JWT auto-signé ne peut pas offrir.
            if (BearerToken(http) is { } token)
            {
                await tokens.RevokeAsync(token, cancellationToken).ConfigureAwait(false);
            }

            return Results.NoContent();
        });

        // Attribution des droits : réservée au super-admin, et hors de l'API des
        // enregistrements. Passer par un PATCH ordinaire permettrait à un compte de s'accorder ses
        // propres permissions dès que la règle de modification est un peu large.
        group.MapPost("/records/{id}/grants", async (
            string collection,
            string id,
            GrantRequest request,
            GrantService grants,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            CollectionEndpoints.RequireSuperuser(user);
            ArgumentNullException.ThrowIfNull(request);

            await grants.AssignAsync(collection, id, request.Roles, request.Permissions, cancellationToken)
                .ConfigureAwait(false);

            return Results.NoContent();
        });

        endpoints.MapGet("/me", (ICurrentUser user) => user.IsAuthenticated
            ? Results.Ok(new
            {
                id = user.Id?.ToString(),
                collectionName = user.CollectionName,
                isSuperuser = user.IsSuperuser,
                permissions = user.Permissions,
                fields = user.Fields,
            })
            : Results.Unauthorized());

        return endpoints;
    }

    internal static string? BearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        // PocketBase accepte le jeton nu ; on accepte les deux formes pour rester compatible avec
        // les clients écrits contre lui.
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..].Trim()
            : header.Trim();
    }
}
