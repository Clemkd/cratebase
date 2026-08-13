using System.Globalization;

namespace Cratebase.Expressions;

/// <summary>
/// Analyse syntaxique du langage de filtre : produit un arbre, jamais du SQL.
/// </summary>
/// <remarks>
/// <para>
/// C'est le même analyseur pour les deux usages du langage : le paramètre <c>?filter=</c> d'une
/// requête, et les règles d'accès d'une collection. Un seul moteur, donc une seule sémantique à
/// sécuriser — c'est ce qui évite qu'une expression autorisée dans un contexte se comporte
/// autrement dans l'autre.
/// </para>
/// <para>
/// L'arbre produit ne référence aucun champ résolu. La liste blanche, la traversée de relations et
/// la compilation en SQL sont l'affaire de l'analyse sémantique, en aval.
/// </para>
/// </remarks>
public static class FilterParser
{
    /// <summary>Profondeur maximale d'imbrication de parenthèses.</summary>
    /// <remarks>
    /// Borne obligatoire : sans elle, une expression profonde fait déborder la pile côté serveur.
    /// C'est un déni de service en une chaîne de caractères.
    /// </remarks>
    public const int MaxDepth = 32;

    /// <summary>Longueur maximale d'une expression, en caractères.</summary>
    public const int MaxLength = 8192;

    /// <summary>
    /// Analyse une expression de filtre.
    /// </summary>
    /// <returns>
    /// L'arbre correspondant, ou <see langword="null"/> si l'expression est vide — ce qui signifie
    /// « aucune contrainte », et non « refuser tout ».
    /// </returns>
    /// <exception cref="FilterSyntaxException">L'expression n'est pas analysable.</exception>
    public static FilterNode? Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        if (expression.Length > MaxLength)
        {
            throw new FilterSyntaxException(
                $"expression trop longue ({expression.Length} caractères, maximum {MaxLength})", 0);
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
                    $"{Current} n'était pas attendu ici", Current.Position);
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
                    $"expression trop imbriquée (maximum {MaxDepth} niveaux)", Current.Position);
            }

            if (Current.Kind is FilterTokenKind.LeftParen)
            {
                _index++;
                var inner = ParseExpression(depth + 1);

                if (Current.Kind is not FilterTokenKind.RightParen)
                {
                    throw new FilterSyntaxException("parenthèse fermante manquante", Current.Position);
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
                    $"opérateur de comparaison attendu, trouvé {Current}", Current.Position);
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
                    throw new FilterSyntaxException($"opérande attendue, trouvé {token}", token.Position);
            }
        }

        private FunctionNode ParseFunctionCall(FilterToken name)
        {
            if (name.Text.Contains('.', StringComparison.Ordinal)
                || name.Text.Contains(':', StringComparison.Ordinal))
            {
                throw new FilterSyntaxException(
                    $"« {name.Text} » n'est pas un nom de fonction valide", name.Position);
            }

            _index++; // parenthèse ouvrante

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
                    "parenthèse fermante manquante après les arguments", Current.Position);
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
                        $"modificateur inconnu « :{suffix} »", token.Position + colon),
                };
            }

            var segments = raw.Split('.', StringSplitOptions.None);

            foreach (var segment in segments)
            {
                if (segment.Length == 0)
                {
                    throw new FilterSyntaxException(
                        $"segment vide dans le chemin « {token.Text} »", token.Position);
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
            _ => throw new FilterSyntaxException($"opérateur inconnu « {symbol} »", position),
        };
    }
}
