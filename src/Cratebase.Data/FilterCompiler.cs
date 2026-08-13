using Cratebase.Core;
using Cratebase.Expressions;

namespace Cratebase.Data;

/// <summary>
/// Compile un arbre de filtre en fragment SQL paramétré.
/// </summary>
/// <remarks>
/// <para>
/// Deux invariants tiennent toute la sécurité du moteur, et ils sont structurels — pas
/// documentaires :
/// </para>
/// <list type="number">
/// <item>
/// <b>Un chemin que le résolveur refuse ne devient jamais du SQL.</b> Il produit une erreur 400.
/// Aucune valeur d'utilisateur n'est concaténée : tout passe par <see cref="SqlWriter.Parameter"/>.
/// </item>
/// <item>
/// <b>Une règle d'accès ne peut être qu'ajoutée à un filtre, jamais alternée.</b> C'est pour cela
/// que la composition passe par <see cref="SqlPredicate.And"/> et qu'aucun <c>Or</c> public
/// n'existe : rendre l'erreur inexprimable vaut mieux que la proscrire.
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
    /// Compile un arbre en prédicat.
    /// </summary>
    /// <param name="node">Arbre, ou <see langword="null"/> pour « aucune contrainte ».</param>
    /// <param name="tableAlias">Alias de la table de la collection racine.</param>
    /// <param name="parameterPrefix">
    /// Préfixe des noms de paramètres. Deux prédicats destinés à être composés doivent recevoir des
    /// préfixes distincts — c'est ce qui permet à <see cref="SqlPredicate.And"/> de les fusionner
    /// sans renommage, donc sans réécriture de texte SQL.
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
    /// Analyse puis compile une expression textuelle.
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
                throw new FilterSyntaxException("nœud d'expression non pris en charge", node.Position);
        }
    }

    private void EmitComparison(ComparisonNode node, SqlWriter writer, string alias)
    {
        var left = ResolveOperand(node.Left, alias);
        var right = ResolveOperand(node.Right, alias);
        var kind = Map(node.Operator);

        // Les deux côtés sont connus sans toucher la base : on tranche à la compilation.
        // C'est le cas de la plupart des règles d'accès (« @request.auth.id != '' »).
        if (left.IsConstant && right.IsConstant)
        {
            var outcome = FilterValueComparer.Evaluate(left.Value, kind, right.Value);
            writer.Raw(outcome ? "1 = 1" : "1 = 0");
            return;
        }

        // On ramène toujours le champ à gauche : un seul cas à écrire au lieu de deux.
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

        // « = null » n'est jamais vrai en SQL : la seule lecture utile est « IS NULL ».
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

        // Sur un champ multi-valué, « ?= » demande « au moins un élément », tout le reste demande
        // « tous les éléments ». C'est la sémantique de PocketBase, et l'inverser élargirait
        // silencieusement les règles portant sur des listes de rôles ou de relations.
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
        // Sur un champ multi-valué, la comparaison porte sur l'élément courant, que le dialecte
        // substituera à ce jeton.
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
            // « champ ~ champ » exigerait de construire le motif en SQL, donc de la concaténation
            // propre à chaque moteur pour un besoin qu'on n'a jamais rencontré. Refus explicite
            // plutôt qu'une traduction approximative.
            throw new FilterSyntaxException(
                "l'opérateur « ~ » exige une valeur littérale à droite", node.Position);
        }

        if (left.Field?.Multiple == true || right.Field?.Multiple == true)
        {
            throw new FilterSyntaxException(
                "comparer deux champs dont l'un est multi-valué n'est pas pris en charge",
                node.Position);
        }

        writer.Raw(left.Sql).Raw(SymbolOf(kind, node.Position)).Raw(right.Sql);
    }

    private Operand ResolveOperand(OperandNode node, string alias) => node switch
    {
        LiteralNode literal => Operand.Constant(literal.Value),
        PathNode path => ResolvePath(path, alias),
        FunctionNode function => throw new FilterSyntaxException(
            $"la fonction « {function.Name} » n'est pas encore prise en charge", function.Position),
        _ => throw new FilterSyntaxException("opérande non prise en charge", node.Position),
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
                    $"méta-champ inconnu « {path} »", path.Position);
        }

        if (path.IsMacro)
        {
            return DateMacros.TryResolve(path.Segments[0], _clock.UtcNow, out var value)
                ? Operand.Constant(value)
                : throw new FilterSyntaxException($"macro inconnue « {path} »", path.Position);
        }

        if (path.IsCollectionPath)
        {
            throw new FilterSyntaxException(
                "les jointures « @collection.* » ne sont pas encore prises en charge", path.Position);
        }

        // Le seul chemin restant est un champ. S'il n'est pas dans le schéma, c'est une erreur
        // d'entrée — jamais une interpolation. C'est la frontière d'injection.
        if (!_resolver.TryResolve(path.Segments, out var field))
        {
            throw new FilterSyntaxException(
                $"« {path} » n'est pas un champ de la collection « {_resolver.RootCollection} »",
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
            "« :length » se compare à un nombre", position),
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
            "l'opérateur « ~ » exige le champ à gauche et une valeur à droite", position),
    };

    private static string SymbolOf(ComparisonOperatorKind kind, int position) => kind switch
    {
        ComparisonOperatorKind.Equal => " = ",
        ComparisonOperatorKind.NotEqual => " <> ",
        ComparisonOperatorKind.GreaterThan => " > ",
        ComparisonOperatorKind.GreaterThanOrEqual => " >= ",
        ComparisonOperatorKind.LessThan => " < ",
        ComparisonOperatorKind.LessThanOrEqual => " <= ",
        _ => throw new FilterSyntaxException("opérateur non applicable ici", position),
    };

    /// <summary>
    /// Jeton que les dialectes remplacent par l'élément courant, dans les comparaisons portant sur
    /// un champ multi-valué.
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
