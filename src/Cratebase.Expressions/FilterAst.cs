namespace Cratebase.Expressions;

/// <summary>Connecteur logique.</summary>
public enum LogicalOperator
{
    /// <summary>Conjonction.</summary>
    And,

    /// <summary>Disjonction.</summary>
    Or,
}

/// <summary>Opérateur de comparaison.</summary>
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

    /// <summary><c>~</c> — contient. Toujours insensible à la casse (voir §5 de la conception).</summary>
    Like,

    /// <summary><c>!~</c> — ne contient pas.</summary>
    NotLike,
}

/// <summary>
/// Modificateur appliqué à un chemin, introduit par <c>:</c>.
/// </summary>
public enum PathModifier
{
    /// <summary>Aucun.</summary>
    None,

    /// <summary><c>:isset</c> — le champ a-t-il été soumis ? Réservé aux chemins <c>@request.*</c>.</summary>
    IsSet,

    /// <summary><c>:length</c> — nombre d'éléments d'un champ multi-valué.</summary>
    Length,

    /// <summary><c>:each</c> — la condition s'applique à chaque élément.</summary>
    Each,

    /// <summary><c>:lower</c> — comparaison en minuscules.</summary>
    Lower,

    /// <summary><c>:changed</c> — le champ a-t-il été soumis <i>et</i> modifié ?</summary>
    Changed,
}

/// <summary>Nœud de l'arbre syntaxique d'un filtre.</summary>
/// <param name="Position">Décalage dans l'expression d'origine, pour les messages d'erreur.</param>
public abstract record FilterNode(int Position);

/// <summary>Combinaison logique de deux sous-expressions.</summary>
public sealed record LogicalNode(
    LogicalOperator Operator,
    FilterNode Left,
    FilterNode Right,
    int Position) : FilterNode(Position);

/// <summary>
/// Comparaison de deux opérandes.
/// </summary>
/// <param name="Left">Opérande de gauche.</param>
/// <param name="Operator">Opérateur de comparaison.</param>
/// <param name="AnyOf">
/// Vrai si l'opérateur était préfixé par <c>?</c>. Sur un champ multi-valué, la comparaison passe
/// alors de « tous les éléments satisfont » à « au moins un élément satisfait ».
/// </param>
/// <param name="Right">Opérande de droite.</param>
/// <param name="Position">Décalage dans l'expression d'origine.</param>
public sealed record ComparisonNode(
    OperandNode Left,
    ComparisonOperator Operator,
    bool AnyOf,
    OperandNode Right,
    int Position) : FilterNode(Position);

/// <summary>Opérande d'une comparaison.</summary>
public abstract record OperandNode(int Position);

/// <summary>
/// Valeur littérale : chaîne, nombre, booléen ou <see langword="null"/>.
/// </summary>
public sealed record LiteralNode(object? Value, int Position) : OperandNode(Position);

/// <summary>
/// Chemin d'accès à une valeur : champ de la collection (<c>title</c>), traversée de relation
/// (<c>author.name</c>), méta-champ de requête (<c>@request.auth.id</c>), jointure
/// (<c>@collection.posts.owner</c>) ou macro de date (<c>@now</c>).
/// </summary>
/// <remarks>
/// Le parseur ne cherche pas à savoir ce que le chemin désigne : c'est l'analyse sémantique qui le
/// résout contre le schéma. Un chemin non résolu est une erreur 400, jamais une interpolation —
/// c'est la frontière d'injection du moteur.
/// </remarks>
public sealed record PathNode(
    IReadOnlyList<string> Segments,
    PathModifier Modifier,
    int Position) : OperandNode(Position)
{
    /// <summary>Le chemin cible-t-il un méta-champ de requête ?</summary>
    public bool IsRequestPath =>
        Segments.Count > 0 && Segments[0].Equals("@request", StringComparison.Ordinal);

    /// <summary>Le chemin cible-t-il une autre collection par jointure ?</summary>
    public bool IsCollectionPath =>
        Segments.Count > 0 && Segments[0].Equals("@collection", StringComparison.Ordinal);

    /// <summary>Le chemin est-il une macro (date, ou autre valeur fournie par le moteur) ?</summary>
    public bool IsMacro =>
        Segments.Count == 1 && Segments[0].StartsWith('@') && !IsRequestPath && !IsCollectionPath;

    /// <summary>Forme textuelle du chemin, telle qu'écrite.</summary>
    public override string ToString() => Modifier is PathModifier.None
        ? string.Join('.', Segments)
        : $"{string.Join('.', Segments)}:{Modifier.ToString().ToLowerInvariant()}";
}

/// <summary>
/// Appel de fonction logique.
/// </summary>
/// <remarks>
/// ⚠️ Les fonctions exposées sont <b>logiques</b>, jamais celles du moteur. PocketBase expose
/// <c>strftime()</c>, qui est du SQLite pur : c'est précisément ce qui interdit d'en migrer une
/// application. Chaque fonction admise ici a une traduction dans chaque dialecte, et la suite de
/// conformité vérifie qu'elles rendent le même résultat.
/// </remarks>
public sealed record FunctionNode(
    string Name,
    IReadOnlyList<OperandNode> Arguments,
    int Position) : OperandNode(Position);
