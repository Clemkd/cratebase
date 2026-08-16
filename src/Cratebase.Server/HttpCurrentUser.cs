using Cratebase.Auth;
using Cratebase.Core;
using Cratebase.Data;
using Microsoft.AspNetCore.Http;

namespace Cratebase.Server;

/// <summary>
/// Resolves the caller from the token carried by the request.
/// </summary>
/// <remarks>
/// The full record is loaded, not just an identifier: the filter language exposes
/// <c>@request.auth.*</c>, which must be able to reach any field of the auth collection —
/// including a field the library's user added afterward.
/// </remarks>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    /// <summary>Key under which the middleware stores the resolved record.</summary>
    public const string ContextKey = "cratebase.auth";

    private readonly IHttpContextAccessor _accessor = accessor
        ?? throw new ArgumentNullException(nameof(accessor));

    private AuthenticatedRecord? Record =>
        _accessor.HttpContext?.Items.TryGetValue(ContextKey, out var value) == true
            ? value as AuthenticatedRecord
            : null;

    /// <inheritdoc />
    public bool IsAuthenticated => Record is not null;

    /// <inheritdoc />
    public bool IsSuperuser => Record?.IsSuperuser == true;

    /// <inheritdoc />
    public RecordId? Id => Record?.Id;

    /// <inheritdoc />
    public string? CollectionName => Record?.Collection;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Permissions => Record?.Permissions ?? [];

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Fields =>
        Record?.Fields ?? System.Collections.Frozen.FrozenDictionary<string, object?>.Empty;
}

/// <summary>
/// Resolves the authentication token and stores the caller in the request context.
/// </summary>
public sealed class CratebaseAuthMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    /// <summary>Processes the request.</summary>
    public async Task InvokeAsync(HttpContext context, AuthTokenStore tokens, AuthService auth)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(auth);

        if (AuthEndpoints.BearerToken(context) is { } token)
        {
            var resolved = await tokens.ResolveAsync(token, context.RequestAborted).ConfigureAwait(false);

            if (resolved is not null)
            {
                // Permissions are re-read from the database on every request: a revoked role takes
                // effect immediately. That's the cost of one indexed read, against a drift that
                // could otherwise last a token's whole lifetime.
                var record = await auth
                    .LoadAsync(resolved.Collection, resolved.RecordId, context.RequestAborted)
                    .ConfigureAwait(false);

                if (record is not null)
                {
                    context.Items[HttpCurrentUser.ContextKey] = record;
                }
            }

            // An invalid token doesn't cause an error here: the request continues anonymously, and
            // it's the endpoint that decides. Refusing outright would break public routes called
            // with an expired token still sitting in a client.
        }

        await _next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Builds the rule-evaluation context from the HTTP request.
/// </summary>
public static class RequestContextFactory
{
    /// <summary>Builds a request's context.</summary>
    public static FilterRequestContext Create(
        HttpContext http,
        ICurrentUser user,
        IReadOnlyDictionary<string, object?>? body = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);

        var headers = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var header in http.Request.Headers)
        {
            // Same normalization as PocketBase: lowercase, hyphens turned into underscores.
            // "X-Forwarded-For" becomes "x_forwarded_for", reachable as
            // "@request.headers.x_forwarded_for".
            headers[header.Key.ToLowerInvariant().Replace('-', '_')] = header.Value.ToString();
        }

        var query = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var parameter in http.Request.Query)
        {
            query[parameter.Key] = parameter.Value.ToString();
        }

        return new FilterRequestContext
        {
            Auth = user,
            Method = http.Request.Method,
            Headers = headers,
            Query = query,
            Body = body ?? new Dictionary<string, object?>(StringComparer.Ordinal),
        };
    }
}
