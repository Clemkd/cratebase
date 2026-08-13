using Cratebase.Core;

namespace Cratebase.Data;

/// <summary>
/// Contexte d'exécution d'une règle ou d'un filtre, source des méta-champs <c>@request.*</c>.
/// </summary>
/// <remarks>
/// Les valeurs sont résolues <b>à la compilation</b> et deviennent des paramètres SQL. Elles ne
/// sont jamais du texte injecté : <c>@request.headers.x_forwarded_for</c> est une chaîne fournie
/// par le client, et elle doit être traitée comme telle.
/// </remarks>
public sealed class FilterRequestContext
{
    /// <summary>Contexte vide, pour les évaluations hors requête HTTP.</summary>
    public static readonly FilterRequestContext None = new();

    /// <summary>Appelant.</summary>
    public ICurrentUser Auth { get; init; } = AnonymousUser.Instance;

    /// <summary>
    /// Nature de l'exécution : <c>default</c>, <c>oauth2</c>, <c>otp</c>, <c>password</c>,
    /// <c>realtime</c> ou <c>protectedFile</c>.
    /// </summary>
    public string Context { get; init; } = "default";

    /// <summary>Verbe HTTP.</summary>
    public string Method { get; init; } = "GET";

    /// <summary>
    /// En-têtes. Les noms sont normalisés en minuscules avec les tirets remplacés par des
    /// soulignés, comme chez PocketBase : <c>X-Forwarded-For</c> devient <c>x_forwarded_for</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Headers { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>Paramètres de requête.</summary>
    public IReadOnlyDictionary<string, string?> Query { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Corps soumis, après application des valeurs par défaut et des hooks.
    /// </summary>
    /// <remarks>
    /// C'est sur cette version-là que <c>createRule</c> s'évalue, jamais sur le corps brut : sinon
    /// un hook qui renseigne le propriétaire arriverait après le contrôle d'accès.
    /// </remarks>
    public IReadOnlyDictionary<string, object?> Body { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// État de l'enregistrement avant modification, quand il y en a un. Sert au modificateur
    /// <c>:changed</c>.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Original { get; init; }

    /// <summary>
    /// Résout un chemin <c>@request.*</c>.
    /// </summary>
    /// <param name="segments">Chemin complet, <c>@request</c> compris.</param>
    /// <param name="value">Valeur résolue.</param>
    /// <returns><see langword="false"/> si le chemin n'est pas un méta-champ reconnu.</returns>
    public bool TryResolve(IReadOnlyList<string> segments, out object? value)
    {
        ArgumentNullException.ThrowIfNull(segments);

        value = null;

        if (segments.Count < 2 || !segments[0].Equals("@request", StringComparison.Ordinal))
        {
            return false;
        }

        switch (segments[1])
        {
            case "context" when segments.Count == 2:
                value = Context;
                return true;

            case "method" when segments.Count == 2:
                value = Method;
                return true;

            case "headers" when segments.Count == 3:
                value = Headers.GetValueOrDefault(segments[2]);
                return true;

            case "query" when segments.Count == 3:
                value = Query.GetValueOrDefault(segments[2]);
                return true;

            case "body" when segments.Count >= 3:
                value = Body.GetValueOrDefault(segments[2]);
                return true;

            case "auth" when segments.Count >= 3:
                value = ResolveAuth(segments[2]);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Indique si un chemin <c>@request.*</c> a effectivement été soumis (modificateur
    /// <c>:isset</c>).
    /// </summary>
    public bool IsSet(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count < 3 || !segments[0].Equals("@request", StringComparison.Ordinal))
        {
            return false;
        }

        return segments[1] switch
        {
            "body" => Body.ContainsKey(segments[2]),
            "query" => Query.ContainsKey(segments[2]),
            "headers" => Headers.ContainsKey(segments[2]),
            "auth" => Auth.IsAuthenticated,
            _ => false,
        };
    }

    /// <summary>
    /// Indique si un champ du corps a été soumis <i>et</i> diffère de l'état antérieur
    /// (modificateur <c>:changed</c>).
    /// </summary>
    public bool HasChanged(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (!IsSet(segments) || Original is null || segments.Count < 3)
        {
            return false;
        }

        var submitted = Body.GetValueOrDefault(segments[2]);
        var original = Original.GetValueOrDefault(segments[2]);

        return !Equals(submitted, original);
    }

    private object? ResolveAuth(string fieldName)
    {
        // Non authentifié : « id » vaut la chaîne vide, jamais null. C'est ce qui fait marcher
        // l'idiome universel « @request.auth.id != '' » comme test d'authentification — avec null,
        // la comparaison SQL ne serait ni vraie ni fausse, et la règle laisserait passer ou
        // bloquerait selon le moteur.
        if (!Auth.IsAuthenticated)
        {
            return fieldName is "id" ? string.Empty : null;
        }

        return fieldName switch
        {
            "id" => Auth.Id?.ToString() ?? string.Empty,
            "collectionName" => Auth.CollectionName,
            _ => Auth.Fields.GetValueOrDefault(fieldName),
        };
    }
}
