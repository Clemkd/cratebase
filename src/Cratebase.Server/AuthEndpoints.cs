using Cratebase.Auth;
using Cratebase.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cratebase.Server;

/// <summary>Body of an authentication request.</summary>
/// <param name="Identity">Email address.</param>
/// <param name="Password">Password.</param>
public sealed record AuthWithPasswordRequest(string Identity, string Password);

/// <summary>Body of a rights grant.</summary>
/// <param name="Roles">Roles to set.</param>
/// <param name="Permissions">Individual overrides to set.</param>
public sealed record GrantRequest(IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

/// <summary>Body of a second factor.</summary>
/// <param name="MfaId">Identifier of the challenge returned by the first factor.</param>
/// <param name="Code">One-time code.</param>
public sealed record AuthWithOtpRequest(string MfaId, string Code);

/// <summary>Body of a two-factor code.</summary>
/// <param name="Code">One-time code.</param>
public sealed record MfaCodeRequest(string Code);

/// <summary>Body of a sign-in through an external provider.</summary>
/// <param name="Provider">Technical name of the provider.</param>
/// <param name="Code">Authorization code received from the provider.</param>
/// <param name="CodeVerifier">PKCE verifier, if the flow uses one.</param>
/// <param name="RedirectUrl">Redirect URL, which must match the one used for authorization.</param>
public sealed record AuthWithOAuth2Request(
    string Provider,
    string Code,
    string? CodeVerifier,
    string RedirectUrl);

/// <summary>
/// Authentication endpoints.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Publishes the authentication routes.</summary>
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

            // Second factor required: 401 and a challenge identifier, PocketBase's own semantics.
            // The 401 matters — a 200 would let a naive client believe the session is open.
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
                    // ⚠️ No client secret, and nothing derived from one. This endpoint is public by
                    // nature: the sign-in screen needs to know which buttons to show.
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
                    $"Provider \"{request.Provider}\" is not configured.");
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

            // A valid code is required to disable: without it, a stolen token would be enough to
            // remove the second factor, which defeats the whole point of the mechanism.
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

            // Rotation: the old token is revoked, a new one is issued. A token renewable forever
            // with no rotation is indistinguishable from a permanent token.
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
            // Actual sign-out: the token disappears from the database. That's what opaque tokens
            // buy, and a self-signed JWT can't offer.
            if (BearerToken(http) is { } token)
            {
                await tokens.RevokeAsync(token, cancellationToken).ConfigureAwait(false);
            }

            return Results.NoContent();
        });

        // Rights assignment: reserved for the superuser, and outside the records API. Going
        // through an ordinary PATCH would let an account grant itself its own permissions as soon
        // as the update rule is even slightly permissive.
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

        // PocketBase accepts the bare token; we accept both forms to stay compatible with clients
        // written against it.
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..].Trim()
            : header.Trim();
    }
}
