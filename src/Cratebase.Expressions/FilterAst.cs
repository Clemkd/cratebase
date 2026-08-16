namespace Cratebase.Expressions;

/// <summary>Logical connector.</summary>
public enum LogicalOperator
{
    /// <summary>Conjunction.</summary>
    And,

    /// <summary>Disjunction.</summary>
    Or,
}

/// <summary>Comparison operator.</summary>
public enum ComparisonOperator
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

    /// <summary><c>~</c> — contains. Always case-insensitive (see design §5).</summary>
    Like,

    /// <summary><c>!~</c> — does not contain.</summary>
    NotLike,
}

/// <summary>
/// Modifier applied to a path, introduced by <c>:</c>.
/// </summary>
public enum PathModifier
{
    /// <summary>None.</summary>
    None,

    /// <summary><c>:isset</c> — was the field submitted? Reserved for <c>@request.*</c> paths.</summary>
    IsSet,

    /// <summary><c>:length</c> — element count of a multi-valued field.</summary>
    Length,

    /// <summary><c>:each</c> — the condition applies to each element.</summary>
    Each,

    /// <summary><c>:lower</c> — lowercase comparison.</summary>
    Lower,

    /// <summary><c>:changed</c> — was the field submitted <i>and</i> changed?</summary>
    Changed,
}

/// <summary>Node of a filter's syntax tree.</summary>
/// <param name="Position">Offset in the original expression, for error messages.</param>
public abstract record FilterNode(int Position);

/// <summary>Logical combination of two sub-expressions.</summary>
public sealed record LogicalNode(
    LogicalOperator Operator,
    FilterNode Left,
    FilterNode Right,
    int Position) : FilterNode(Position);

/// <summary>
/// Comparison of two operands.
/// </summary>
/// <param name="Left">Left operand.</param>
/// <param name="Operator">Comparison operator.</param>
/// <param name="AnyOf">
/// True if the operator was prefixed with <c>?</c>. On a multi-valued field, the comparison then
/// switches from "every element satisfies" to "at least one element satisfies".
/// </param>
/// <param name="Right">Right operand.</param>
/// <param name="Position">Offset in the original expression.</param>
public sealed record ComparisonNode(
    OperandNode Left,
    ComparisonOperator Operator,
    bool AnyOf,
    OperandNode Right,
    int Position) : FilterNode(Position);

/// <summary>Operand of a comparison.</summary>
public abstract record OperandNode(int Position);

/// <summary>
/// Literal value: string, number, boolean, or <see langword="null"/>.
/// </summary>
public sealed record LiteralNode(object? Value, int Position) : OperandNode(Position);

/// <summary>
/// Access path to a value: a collection field (<c>title</c>), a relation traversal
/// (<c>author.name</c>), a request meta-field (<c>@request.auth.id</c>), a join
/// (<c>@collection.posts.owner</c>), or a date macro (<c>@now</c>).
/// </summary>
/// <remarks>
/// The parser does not try to know what the path designates: semantic analysis resolves it
/// against the schema. An unresolved path is a 400 error, never an interpolation — this is the
/// engine's injection boundary.
/// </remarks>
public sealed record PathNode(
    IReadOnlyList<string> Segments,
    PathModifier Modifier,
    int Position) : OperandNode(Position)
{
    /// <summary>Does the path target a request meta-field?</summary>
    public bool IsRequestPath =>
        Segments.Count > 0 && Segments[0].Equals("@request", StringComparison.Ordinal);

    /// <summary>Does the path target another collection via a join?</summary>
    public bool IsCollectionPath =>
        Segments.Count > 0 && Segments[0].Equals("@collection", StringComparison.Ordinal);

    /// <summary>Is the path a macro (date, or another engine-supplied value)?</summary>
    public bool IsMacro =>
        Segments.Count == 1 && Segments[0].StartsWith('@') && !IsRequestPath && !IsCollectionPath;

    /// <summary>Textual form of the path, as written.</summary>
    public override string ToString() => Modifier is PathModifier.None
        ? string.Join('.', Segments)
        : $"{string.Join('.', Segments)}:{Modifier.ToString().ToLowerInvariant()}";
}

/// <summary>
/// Logical function call.
/// </summary>
/// <remarks>
/// ⚠️ The exposed functions are <b>logical</b>, never the engine's own. PocketBase exposes
/// <c>strftime()</c>, which is pure SQLite: precisely what forbids migrating an application off
/// it. Every function admitted here has a translation in each dialect, and the conformance suite
/// verifies they return the same result.
/// </remarks>
public sealed record FunctionNode(
    string Name,
    IReadOnlyList<OperandNode> Arguments,
    int Position) : OperandNode(Position);
