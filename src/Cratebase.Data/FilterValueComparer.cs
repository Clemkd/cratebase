using System.Globalization;

namespace Cratebase.Data;

/// <summary>
/// Comparaison de deux valeurs constantes, pour le repli à la compilation.
/// </summary>
/// <remarks>
/// Sert quand les deux opérandes sont connues sans toucher la base — cas très fréquent dans les
/// règles d'accès : <c>@request.auth.id != ''</c>, <c>@request.method = 'GET'</c>. Le résultat
/// devient <c>1=1</c> ou <c>1=0</c>, ce qui évite un paramètre et, surtout, garantit que la
/// sémantique ne dépend pas des règles de conversion implicite du moteur — qui diffèrent entre
/// SQLite et PostgreSQL précisément sur les comparaisons hétérogènes.
/// </remarks>
public static class FilterValueComparer
{
    /// <summary>Évalue une comparaison entre deux valeurs constantes.</summary>
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
            // Une comparaison d'ordre impliquant l'absence de valeur n'a pas de sens : on la rend
            // fausse dans les deux sens, comme le ferait un NULL SQL.
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
/// Opérateur de comparaison, redéclaré ici pour que <c>Cratebase.Data</c> n'impose pas au
/// consommateur de connaître l'AST.
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
