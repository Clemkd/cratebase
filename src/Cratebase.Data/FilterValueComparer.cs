using System.Globalization;

namespace Cratebase.Data;

/// <summary>
/// Comparison of two constant values, for compile-time folding.
/// </summary>
/// <remarks>
/// Used when both operands are known without touching the database — a very common case in access
/// rules: <c>@request.auth.id != ''</c>, <c>@request.method = 'GET'</c>. The result becomes
/// <c>1=1</c> or <c>1=0</c>, which avoids a parameter and, more importantly, guarantees that the
/// semantics does not depend on the engine's implicit conversion rules — which differ between
/// SQLite and PostgreSQL precisely on heterogeneous comparisons.
/// </remarks>
public static class FilterValueComparer
{
    /// <summary>Evaluates a comparison between two constant values.</summary>
    public static bool Evaluate(object? left, ComparisonOperatorKind kind, object? right) => kind switch
    {
        ComparisonOperatorKind.Equal => AreEqual(left, right),
        ComparisonOperatorKind.NotEqual => !AreEqual(left, right),
        ComparisonOperatorKind.GreaterThan => Compare(left, right) > 0,
        ComparisonOperatorKind.GreaterThanOrEqual => Compare(left, right) >= 0,
        ComparisonOperatorKind.LessThan => Compare(left, right) < 0,
        ComparisonOperatorKind.LessThanOrEqual => Compare(left, right) <= 0,
        ComparisonOperatorKind.Like => Contains(left, right),
        ComparisonOperatorKind.NotLike => !Contains(left, right),
        _ => false,
    };

    private static bool AreEqual(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is bool || right is bool)
        {
            return ToBoolean(left) == ToBoolean(right);
        }

        if (left is double leftNumber && right is double rightNumber)
        {
            return leftNumber.Equals(rightNumber);
        }

        return string.Equals(ToText(left), ToText(right), StringComparison.Ordinal);
    }

    private static int Compare(object? left, object? right)
    {
        if (left is null || right is null)
        {
            // An order comparison involving a missing value is meaningless: made false both ways,
            // as a SQL NULL would behave.
            return left is null && right is null ? 0 : int.MinValue / 2;
        }

        if (left is double leftNumber && right is double rightNumber)
        {
            return leftNumber.CompareTo(rightNumber);
        }

        return string.CompareOrdinal(ToText(left), ToText(right));
    }

    private static bool Contains(object? haystack, object? needle)
    {
        if (haystack is null || needle is null)
        {
            return false;
        }

        return ToText(haystack).Contains(ToText(needle), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ToBoolean(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        double number => number != 0,
        string text => text is not ("" or "0" or "false"),
        _ => true,
    };

    private static string ToText(object value) => value switch
    {
        string text => text,
        bool flag => flag ? "true" : "false",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

/// <summary>
/// Comparison operator, redeclared here so <c>Cratebase.Data</c> doesn't force the consumer to
/// know about the AST.
/// </summary>
public enum ComparisonOperatorKind
{
    /// <summary><c>=</c></summary>
    Equal,

    /// <summary><c>!=</c></summary>
    NotEqual,

    /// <summary><c>&gt;</c></summary>
    GreaterThan,

    /// <summary><c>&gt;=</c></summary>
    GreaterThanOrEqual,

    /// <summary><c>&lt;</c></summary>
    LessThan,

    /// <summary><c>&lt;=</c></summary>
    LessThanOrEqual,

    /// <summary><c>~</c></summary>
    Like,

    /// <summary><c>!~</c></summary>
    NotLike,
}
