using Cratebase.Core;

namespace Cratebase.Data;

/// <summary>
/// Execution context of a rule or filter, the source of <c>@request.*</c> meta-fields.
/// </summary>
/// <remarks>
/// Values are resolved <b>at compile time</b> and become SQL parameters. They are never injected
/// text: <c>@request.headers.x_forwarded_for</c> is a string supplied by the client, and must be
/// treated as such.
/// </remarks>
public sealed class FilterRequestContext
{
    /// <summary>Empty context, for evaluations outside an HTTP request.</summary>
    public static readonly FilterRequestContext None = new();

    /// <summary>Caller.</summary>
    public ICurrentUser Auth { get; init; } = AnonymousUser.Instance;

    /// <summary>
    /// Nature of the execution: <c>default</c>, <c>oauth2</c>, <c>otp</c>, <c>password</c>,
    /// <c>realtime</c>, or <c>protectedFile</c>.
    /// </summary>
    public string Context { get; init; } = "default";

    /// <summary>HTTP verb.</summary>
    public string Method { get; init; } = "GET";

    /// <summary>
    /// Headers. Names are normalized to lowercase with dashes replaced by underscores, as in
    /// PocketBase: <c>X-Forwarded-For</c> becomes <c>x_forwarded_for</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Headers { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>Query parameters.</summary>
    public IReadOnlyDictionary<string, string?> Query { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Submitted body, after defaults and hooks have been applied.
    /// </summary>
    /// <remarks>
    /// <c>createRule</c> evaluates against this version, never against the raw body: otherwise a
    /// hook that fills in the owner would run after the access check.
    /// </remarks>
    public IReadOnlyDictionary<string, object?> Body { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// State of the record before modification, when there is one. Used by the <c>:changed</c>
    /// modifier.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Original { get; init; }

    /// <summary>
    /// Resolves an <c>@request.*</c> path.
    /// </summary>
    /// <param name="segments">Full path, including <c>@request</c>.</param>
    /// <param name="value">Resolved value.</param>
    /// <returns><see langword="false"/> if the path is not a recognized meta-field.</returns>
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
    /// Indicates whether an <c>@request.*</c> path was actually submitted (the <c>:isset</c>
    /// modifier).
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
    /// Indicates whether a body field was submitted <i>and</i> differs from its prior state
    /// (the <c>:changed</c> modifier).
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
        // Unauthenticated: "id" is the empty string, never null. This is what makes the universal
        // idiom "@request.auth.id != ''" work as an authentication test — with null, the SQL
        // comparison would be neither true nor false, and the rule would pass or block depending
        // on the engine.
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
