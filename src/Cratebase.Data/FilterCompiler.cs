using Cratebase.Core;
using Cratebase.Expressions;

namespace Cratebase.Data;

/// <summary>
/// Compiles a filter tree into a parameterized SQL fragment.
/// </summary>
/// <remarks>
/// <para>
/// Two invariants carry the engine's whole security, and they are structural — not documentary:
/// </para>
/// <list type="number">
/// <item>
/// <b>A path the resolver refuses never becomes SQL.</b> It produces a 400 error. No user value is
/// ever concatenated: everything goes through <see cref="SqlWriter.Parameter"/>.
/// </item>
/// <item>
/// <b>An access rule can only be added to a filter, never alternated with it.</b> That's why
/// composition goes through <see cref="SqlPredicate.And"/> and no public <c>Or</c> exists: making
/// the mistake unexpressible beats merely forbidding it.
/// </item>
/// </list>
/// </remarks>
public sealed class FilterCompiler(
    ISqlDialect dialect,
    IQueryFieldResolver resolver,
    FilterRequestContext request,
    IClock clock)
{
    private readonly ISqlDialect _dialect = dialect
        ?? throw new ArgumentNullException(nameof(dialect));

    private readonly IQueryFieldResolver _resolver = resolver
        ?? throw new ArgumentNullException(nameof(resolver));

    private readonly FilterRequestContext _request = request
        ?? throw new ArgumentNullException(nameof(request));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Compiles a tree into a predicate.
    /// </summary>
    /// <param name="node">Tree, or <see langword="null"/> for "no constraint".</param>
    /// <param name="tableAlias">Alias of the root collection's table.</param>
    /// <param name="parameterPrefix">
    /// Prefix for parameter names. Two predicates meant to be composed must receive distinct
    /// prefixes — that's what lets <see cref="SqlPredicate.And"/> merge them without renaming, and
    /// therefore without rewriting SQL text.
    /// </param>
    public SqlPredicate Compile(FilterNode? node, string tableAlias, string parameterPrefix = "p")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterPrefix);

        if (node is null)
        {
            return SqlPredicate.Unconstrained;
        }

        var writer = new SqlWriter(_dialect, parameterPrefix);
        Emit(node, writer, tableAlias);

        return new SqlPredicate(writer.Build());
    }

    /// <summary>
    /// Parses then compiles a textual expression.
    /// </summary>
    public SqlPredicate Compile(string? expression, string tableAlias, string parameterPrefix = "p") =>
        Compile(FilterParser.Parse(expression), tableAlias, parameterPrefix);

    private void Emit(FilterNode node, SqlWriter writer, string alias)
    {
        switch (node)
        {
            case LogicalNode logical:
                writer.Raw("(");
                Emit(logical.Left, writer, alias);
                writer.Raw(logical.Operator is LogicalOperator.And ? " AND " : " OR ");
                Emit(logical.Right, writer, alias);
                writer.Raw(")");
                return;

            case ComparisonNode comparison:
                EmitComparison(comparison, writer, alias);
                return;

            default:
                throw new FilterSyntaxException("unsupported expression node", node.Position);
        }
    }

    private void EmitComparison(ComparisonNode node, SqlWriter writer, string alias)
    {
        var left = ResolveOperand(node.Left, alias);
        var right = ResolveOperand(node.Right, alias);
        var kind = Map(node.Operator);

        // Both sides are known without touching the database: decide it at compile time. This is
        // the case for most access rules ("@request.auth.id != '' ").
        if (left.IsConstant && right.IsConstant)
        {
            var outcome = FilterValueComparer.Evaluate(left.Value, kind, right.Value);
            writer.Raw(outcome ? "1 = 1" : "1 = 0");
            return;
        }

        // Always bring the field to the left: one case to write instead of two.
        if (left.IsConstant)
        {
            (left, right) = (right, left);
            kind = Flip(kind, node.Position);
        }

        if (right.IsConstant)
        {
            EmitFieldToValue(left, kind, right.Value, node, writer);
            return;
        }

        EmitFieldToField(left, kind, right, node, writer);
    }

    private void EmitFieldToValue(
        Operand field,
        ComparisonOperatorKind kind,
        object? value,
        ComparisonNode node,
        SqlWriter writer)
    {
        if (field.Modifier is PathModifier.Length)
        {
            writer.Raw(_dialect.ArrayLength(field.Sql))
                  .Raw(SymbolOf(kind, node.Position))
                  .Parameter(CoerceNumber(value, node.Position));

            return;
        }

        // "= null" is never true in SQL: the only useful reading is "IS NULL".
        if (value is null && kind is ComparisonOperatorKind.Equal or ComparisonOperatorKind.NotEqual)
        {
            writer.Raw(field.Sql)
                  .Raw(kind is ComparisonOperatorKind.Equal ? " IS NULL" : " IS NOT NULL");

            return;
        }

        var descriptor = field.Field!;
        var multiple = descriptor.Multiple && field.Modifier is not PathModifier.Each;
        var comparison = BuildValueComparison(field, kind, value, node, writer, multiple);

        if (!descriptor.Multiple)
        {
            writer.Raw(comparison);
            return;
        }

        // On a multi-valued field, "?=" asks for "at least one element", everything else asks for
        // "every element". This is PocketBase's semantics, and reversing it would silently widen
        // rules built on role or relation lists.
        writer.Raw(node.AnyOf
            ? _dialect.AnyElementMatches(field.Sql, comparison)
            : _dialect.AllElementsMatch(field.Sql, comparison));
    }

    private string BuildValueComparison(
        Operand field,
        ComparisonOperatorKind kind,
        object? value,
        ComparisonNode node,
        SqlWriter writer,
        bool multiple)
    {
        // On a multi-valued field, the comparison targets the current element, which the dialect
        // will substitute for this token.
        var target = multiple ? ElementPlaceholder : field.Sql;
        var descriptor = field.Field!;

        if (kind is ComparisonOperatorKind.Like or ComparisonOperatorKind.NotLike)
        {
            var pattern = writer.AddParameter(LikePattern.Contains(AsText(value)));

            return _dialect.LikeExpression(target, pattern, kind is ComparisonOperatorKind.NotLike);
        }

        if (field.Modifier is PathModifier.Lower)
        {
            var lowered = writer.AddParameter(AsText(value)?.ToLowerInvariant());

            return _dialect.Lower(target) + SymbolOf(kind, node.Position) + lowered;
        }

        var storage = _dialect.ToStorage(descriptor.Type, multiple: false, value);
        var placeholder = writer.AddParameter(storage);

        return target + SymbolOf(kind, node.Position) + placeholder;
    }

    private static void EmitFieldToField(
        Operand left,
        ComparisonOperatorKind kind,
        Operand right,
        ComparisonNode node,
        SqlWriter writer)
    {
        if (kind is ComparisonOperatorKind.Like or ComparisonOperatorKind.NotLike)
        {
            // "field ~ field" would require building the pattern in SQL, hence concatenation
            // specific to each engine, for a need we've never actually run into. Explicit refusal
            // rather than an approximate translation.
            throw new FilterSyntaxException(
                "the \"~\" operator requires a literal value on the right", node.Position);
        }

        if (left.Field?.Multiple == true || right.Field?.Multiple == true)
        {
            throw new FilterSyntaxException(
                "comparing two fields where one is multi-valued is not supported",
                node.Position);
        }

        writer.Raw(left.Sql).Raw(SymbolOf(kind, node.Position)).Raw(right.Sql);
    }

    private Operand ResolveOperand(OperandNode node, string alias) => node switch
    {
        LiteralNode literal => Operand.Constant(literal.Value),
        PathNode path => ResolvePath(path, alias),
        FunctionNode function => throw new FilterSyntaxException(
            $"function \"{function.Name}\" is not yet supported", function.Position),
        _ => throw new FilterSyntaxException("unsupported operand", node.Position),
    };

    private Operand ResolvePath(PathNode path, string alias)
    {
        if (path.Modifier is PathModifier.IsSet)
        {
            return Operand.Constant(_request.IsSet(path.Segments));
        }

        if (path.Modifier is PathModifier.Changed)
        {
            return Operand.Constant(_request.HasChanged(path.Segments));
        }

        if (path.IsRequestPath)
        {
            return _request.TryResolve(path.Segments, out var value)
                ? Operand.Constant(value)
                : throw new FilterSyntaxException(
                    $"unknown meta-field \"{path}\"", path.Position);
        }

        if (path.IsMacro)
        {
            return DateMacros.TryResolve(path.Segments[0], _clock.UtcNow, out var value)
                ? Operand.Constant(value)
                : throw new FilterSyntaxException($"unknown macro \"{path}\"", path.Position);
        }

        if (path.IsCollectionPath)
        {
            throw new FilterSyntaxException(
                "\"@collection.*\" joins are not yet supported", path.Position);
        }

        // The only path left is a field. If it isn't in the schema, that's an input error — never
        // an interpolation. This is the injection boundary.
        if (!_resolver.TryResolve(path.Segments, out var field))
        {
            throw new FilterSyntaxException(
                $"\"{path}\" is not a field of collection \"{_resolver.RootCollection}\"",
                path.Position);
        }

        var sql = _dialect.QuoteIdentifier(alias) + "." + _dialect.QuoteIdentifier(field.ColumnName);

        return Operand.FromField(sql, field, path.Modifier);
    }

    private static double CoerceNumber(object? value, int position) => value switch
    {
        double number => number,
        string text when double.TryParse(text, out var parsed) => parsed,
        _ => throw new FilterSyntaxException(
            "\":length\" compares against a number", position),
    };

    private static string? AsText(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static ComparisonOperatorKind Map(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equal => ComparisonOperatorKind.Equal,
        ComparisonOperator.NotEqual => ComparisonOperatorKind.NotEqual,
        ComparisonOperator.GreaterThan => ComparisonOperatorKind.GreaterThan,
        ComparisonOperator.GreaterThanOrEqual => ComparisonOperatorKind.GreaterThanOrEqual,
        ComparisonOperator.LessThan => ComparisonOperatorKind.LessThan,
        ComparisonOperator.LessThanOrEqual => ComparisonOperatorKind.LessThanOrEqual,
        ComparisonOperator.Like => ComparisonOperatorKind.Like,
        _ => ComparisonOperatorKind.NotLike,
    };

    private static ComparisonOperatorKind Flip(ComparisonOperatorKind kind, int position) => kind switch
    {
        ComparisonOperatorKind.Equal => ComparisonOperatorKind.Equal,
        ComparisonOperatorKind.NotEqual => ComparisonOperatorKind.NotEqual,
        ComparisonOperatorKind.GreaterThan => ComparisonOperatorKind.LessThan,
        ComparisonOperatorKind.GreaterThanOrEqual => ComparisonOperatorKind.LessThanOrEqual,
        ComparisonOperatorKind.LessThan => ComparisonOperatorKind.GreaterThan,
        ComparisonOperatorKind.LessThanOrEqual => ComparisonOperatorKind.GreaterThanOrEqual,
        _ => throw new FilterSyntaxException(
            "the \"~\" operator requires the field on the left and a value on the right", position),
    };

    private static string SymbolOf(ComparisonOperatorKind kind, int position) => kind switch
    {
        ComparisonOperatorKind.Equal => " = ",
        ComparisonOperatorKind.NotEqual => " <> ",
        ComparisonOperatorKind.GreaterThan => " > ",
        ComparisonOperatorKind.GreaterThanOrEqual => " >= ",
        ComparisonOperatorKind.LessThan => " < ",
        ComparisonOperatorKind.LessThanOrEqual => " <= ",
        _ => throw new FilterSyntaxException("operator not applicable here", position),
    };

    /// <summary>
    /// Token that dialects replace with the current element, in comparisons targeting a
    /// multi-valued field.
    /// </summary>
    public const string ElementPlaceholder = "{element}";

    private readonly record struct Operand(
        string Sql,
        bool IsConstant,
        object? Value,
        ResolvedField? Field,
        PathModifier Modifier)
    {
        public static Operand Constant(object? value) =>
            new(string.Empty, IsConstant: true, value, null, PathModifier.None);

        public static Operand FromField(string sql, ResolvedField field, PathModifier modifier) =>
            new(sql, IsConstant: false, null, field, modifier);
    }
}
