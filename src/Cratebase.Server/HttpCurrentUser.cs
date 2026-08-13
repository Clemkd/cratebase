using Cratebase.Auth;
using Cratebase.Core;
using Cratebase.Data;
using Microsoft.AspNetCore.Http;

namespace Cratebase.Server;

/// <summary>
/// Résout l'appelant depuis le jeton porté par la requête.
/// </summary>
/// <remarks>
/// L'enregistrement complet est chargé, et pas seulement un identifiant : le langage de filtre
/// expose <c>@request.auth.*</c>, qui doit pouvoir atteindre n'importe quel champ de la collection
/// d'auth — y compris un champ ajouté après coup par l'utilisateur de la librairie.
/// </remarks>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    /// <summary>Clé sous laquelle le middleware dépose l'enregistrement résolu.</summary>
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
/// Résout le jeton d'authentification et dépose l'appelant dans le contexte de la requête.
/// </summary>
public sealed class CratebaseAuthMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    /// <summary>Traite la requête.</summary>
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
                // Les droits sont relus à chaque requête depuis la base : un rôle retiré prend
                // effet immédiatement. C'est le coût d'une lecture indexée, contre un décalage
                // pouvant durer toute la vie d'un jeton.
                var record = await auth
                    .LoadAsync(resolved.Collection, resolved.RecordId, context.RequestAborted)
                    .ConfigureAwait(false);

                if (record is not null)
                {
                    context.Items[HttpCurrentUser.ContextKey] = record;
                }
            }

            // Un jeton invalide ne provoque pas d'erreur ici : la requête continue en anonyme, et
            // c'est l'endpoint qui décide. Refuser d'emblée casserait les routes publiques appelées
            // avec un jeton périmé encore présent dans un client.
        }

        await _next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Construit le contexte d'évaluation des règles depuis la requête HTTP.
/// </summary>
public static class RequestContextFactory
{
    /// <summary>Construit le contexte d'une requête.</summary>
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
            // Normalisation identique à celle de PocketBase : minuscules, tirets en soulignés.
            // « X-Forwarded-For » devient « x_forwarded_for », donc atteignable en
            // « @request.headers.x_forwarded_for ».
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
