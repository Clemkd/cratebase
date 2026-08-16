namespace Cratebase.Core;

/// <summary>
/// Compares a held permission against a required permission, with wildcards.
/// </summary>
/// <remarks>
/// <para>
/// <b>The wildcard covers only one level.</b> <c>posts.*</c> covers <c>posts.write</c> but not
/// <c>posts.comments.moderate</c>. This is <c>instacontent</c>'s choice, kept as-is: a recursive
/// wildcard means granting <c>posts.*</c> today silently grants, tomorrow, a more sensitive
/// permission added under that root. Nobody reviews roles at that moment.
/// </para>
/// <para>
/// ⚠️ This matcher has a TypeScript twin in <c>@cratebase/client</c>, used to hide UI. The two
/// share a common test-case set (<c>permission-cases.json</c>): a divergence between them means a
/// button shows while the API will refuse, or the reverse. Any change here ripples there, and the
/// case set proves it.
/// </para>
/// </remarks>
public static class PermissionMatcher
{
    private const char Separator = '.';
    private const string Wildcard = "*";

    /// <summary>
    /// Indicates whether the set of held permissions satisfies the required permission.
    /// </summary>
    public static bool IsSatisfied(IReadOnlyCollection<string> held, string required)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentException.ThrowIfNullOrWhiteSpace(required);

        foreach (var candidate in held)
        {
            if (Covers(candidate, required))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Indicates whether <paramref name="held"/> covers <paramref name="required"/>.
    /// </summary>
    public static bool Covers(string? held, string required)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(required);

        if (string.IsNullOrWhiteSpace(held))
        {
            return false;
        }

        if (string.Equals(held, required, StringComparison.Ordinal))
        {
            return true;
        }

        // The terminal wildcard covers exactly one more segment, never more.
        if (!held.EndsWith(Separator + Wildcard, StringComparison.Ordinal))
        {
            return false;
        }

        var prefix = held.AsSpan(0, held.Length - 1);            // "posts.*" -> "posts."
        var target = required.AsSpan();

        if (!target.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = target[prefix.Length..];

        return remainder.Length > 0 && !remainder.Contains(Separator);
    }
}
