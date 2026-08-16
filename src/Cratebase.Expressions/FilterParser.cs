using System.Globalization;

namespace Cratebase.Expressions;

/// <summary>
/// Syntax analysis of the filter language: produces a tree, never SQL.
/// </summary>
/// <remarks>
/// <para>
/// This is the same parser for both uses of the language: a request's <c>?filter=</c> parameter,
/// and a collection's access rules. One engine, so one semantics to secure — this is what stops an
/// expression allowed in one context from behaving differently in the other.
/// </para>
/// <para>
/// The produced tree references no resolved field. The allow-list, relation traversal, and
/// compilation to SQL are semantic analysis's job, downstream.
/// </para>
/// </remarks>
public static class FilterParser
{
    /// <summary>Maximum nesting depth of parentheses.</summary>
    /// <remarks>
    /// A mandatory bound: without it, a deep expression overflows the server-side stack. That's a
    /// denial of service in a string.
    /// </remarks>
    public const int MaxDepth = 32;

    /// <summary>Maximum length of an expression, in characters.</summary>
    public const int MaxLength = 8192;

    /// <summary>
    /// Parses a filter expression.
    /// </summary>
    /// <returns>
    /// The corresponding tree, or <see langword="null"/> if the expression is empty — meaning "no
    /// constraint", not "reject everything".
    /// </returns>
    /// <exception cref="FilterSyntaxException">The expression is not parsable.</exception>
    public static FilterNode? Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        if (expression.Length > MaxLength)
        {
            throw new FilterSyntaxException(
                $"expression too long ({expression.Length} characters, maximum {MaxLength})", 0);
        }

        var tokens = new FilterLexer(expression).Tokenize();
        var parser = new Walker(tokens);
        var node = parser.ParseExpression(depth: 0);

        parser.ExpectEnd();

        return node;
    }

    private sealed class Walker(IReadOnlyList<FilterToken> tokens)
    {
        private int _index;

        private FilterToken Current => tokens[_index];

        public void ExpectEnd()
        {
            if (Current.Kind is not FilterTokenKind.Eof)
            {
                throw new FilterSyntaxException(
                    $"{Current} was not expected here", Current.Position);
            }
        }

        public FilterNode ParseExpression(int depth) => ParseOr(depth);

        private FilterNode ParseOr(int depth)
        {
            var left = ParseAnd(depth);

            while (Current.Kind is FilterTokenKind.Or)
            {
                var position = Current.Position;
                _index++;
                var right = ParseAnd(depth);
                left = new LogicalNode(LogicalOperator.Or, left, right, position);
            }

            return left;
        }

        private FilterNode ParseAnd(int depth)
        {
            var left = ParsePrimary(depth);

            while (Current.Kind is FilterTokenKind.And)
            {
                var position = Current.Position;
                _index++;
                var right = ParsePrimary(depth);
                left = new LogicalNode(LogicalOperator.And, left, right, position);
            }

            return left;
        }

        private FilterNode ParsePrimary(int depth)
        {
            if (depth >= MaxDepth)
            {
                throw new FilterSyntaxException(
                    $"expression too deeply nested (maximum {MaxDepth} levels)", Current.Position);
            }

            if (Current.Kind is FilterTokenKind.LeftParen)
            {
                _index++;
                var inner = ParseExpression(depth + 1);

                if (Current.Kind is not FilterTokenKind.RightParen)
                {
                    throw new FilterSyntaxException("missing closing parenthesis", Current.Position);
                }

                _index++;
                return inner;
            }

            return ParseComparison();
        }

        private ComparisonNode ParseComparison()
        {
            var left = ParseOperand();

            if (Current.Kind is not FilterTokenKind.Operator)
            {
                throw new FilterSyntaxException(
                    $"comparison operator expected, found {Current}", Current.Position);
            }

            var operatorToken = Current;
            _index++;

            var right = ParseOperand();

            var anyOf = operatorToken.Text.StartsWith('?');
            var symbol = anyOf ? operatorToken.Text[1..] : operatorToken.Text;

            return new ComparisonNode(
                left,
                MapOperator(symbol, operatorToken.Position),
                anyOf,
                right,
                operatorToken.Position);
        }

        private OperandNode ParseOperand()
        {
            var token = Current;

            switch (token.Kind)
            {
                case FilterTokenKind.StringLiteral:
                    _index++;
                    return new LiteralNode(token.Text, token.Position);

                case FilterTokenKind.Number:
                    _index++;
                    return new LiteralNode(
                        double.Parse(token.Text, CultureInfo.InvariantCulture), token.Position);

                case FilterTokenKind.True:
                    _index++;
                    return new LiteralNode(true, token.Position);

                case FilterTokenKind.False:
                    _index++;
                    return new LiteralNode(false, token.Position);

                case FilterTokenKind.Null:
                    _index++;
                    return new LiteralNode(null, token.Position);

                case FilterTokenKind.Identifier:
                    _index++;
                    return Current.Kind is FilterTokenKind.LeftParen
                        ? ParseFunctionCall(token)
                        : ParsePath(token);

                default:
                    throw new FilterSyntaxException($"operand expected, found {token}", token.Position);
            }
        }

        private FunctionNode ParseFunctionCall(FilterToken name)
        {
            if (name.Text.Contains('.', StringComparison.Ordinal)
                || name.Text.Contains(':', StringComparison.Ordinal))
            {
                throw new FilterSyntaxException(
                    $"\"{name.Text}\" is not a valid function name", name.Position);
            }

            _index++; // opening parenthesis

            var arguments = new List<OperandNode>();

            if (Current.Kind is not FilterTokenKind.RightParen)
            {
                while (true)
                {
                    arguments.Add(ParseOperand());

                    if (Current.Kind is not FilterTokenKind.Comma)
                    {
                        break;
                    }

                    _index++;
                }
            }

            if (Current.Kind is not FilterTokenKind.RightParen)
            {
                throw new FilterSyntaxException(
                    "missing closing parenthesis after arguments", Current.Position);
            }

            _index++;

            return new FunctionNode(name.Text, arguments, name.Position);
        }

        private static PathNode ParsePath(FilterToken token)
        {
            var raw = token.Text;
            var modifier = PathModifier.None;

            var colon = raw.IndexOf(':', StringComparison.Ordinal);

            if (colon >= 0)
            {
                var suffix = raw[(colon + 1)..];
                raw = raw[..colon];

                modifier = suffix switch
                {
                    "isset" => PathModifier.IsSet,
                    "length" => PathModifier.Length,
                    "each" => PathModifier.Each,
                    "lower" => PathModifier.Lower,
                    "changed" => PathModifier.Changed,
                    _ => throw new FilterSyntaxException(
                        $"unknown modifier \":{suffix}\"", token.Position + colon),
                };
            }

            var segments = raw.Split('.', StringSplitOptions.None);

            foreach (var segment in segments)
            {
                if (segment.Length == 0)
                {
                    throw new FilterSyntaxException(
                        $"empty segment in path \"{token.Text}\"", token.Position);
                }
            }

            return new PathNode(segments, modifier, token.Position);
        }

        private static ComparisonOperator MapOperator(string symbol, int position) => symbol switch
        {
            "=" => ComparisonOperator.Equal,
            "!=" => ComparisonOperator.NotEqual,
            ">" => ComparisonOperator.GreaterThan,
            ">=" => ComparisonOperator.GreaterThanOrEqual,
            "<" => ComparisonOperator.LessThan,
            "<=" => ComparisonOperator.LessThanOrEqual,
            "~" => ComparisonOperator.Like,
            "!~" => ComparisonOperator.NotLike,
            _ => throw new FilterSyntaxException($"unknown operator \"{symbol}\"", position),
        };
    }
}
