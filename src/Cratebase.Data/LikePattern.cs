using System.Text;

namespace Cratebase.Data;

/// <summary>
/// Building the <c>~</c> operator's patterns.
/// </summary>
/// <remarks>
/// The operator automatically wraps the right-hand operand in wildcards, as in PocketBase:
/// <c>title ~ 'hello'</c> searches for <c>%hello%</c>. This means <b>escaping the wildcards the
/// user themselves wrote</b>, otherwise a search for "100%" or "a_b" returns anything — and, on an
/// access rule, widens the scope.
/// </remarks>
public static class LikePattern
{
    /// <summary>Escape character, declared by the <c>ESCAPE</c> clause.</summary>
    public const char EscapeCharacter = '\\';

    /// <summary>
    /// Builds a "contains" pattern, with the user's wildcards escaped.
    /// </summary>
    public static string Contains(string? value) => "%" + Escape(value) + "%";

    /// <summary>Escapes <c>LIKE</c> metacharacters in a value.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 8);

        foreach (var current in value)
        {
            if (current is '%' or '_' or EscapeCharacter)
            {
                builder.Append(EscapeCharacter);
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}
