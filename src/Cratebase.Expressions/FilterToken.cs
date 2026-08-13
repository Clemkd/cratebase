namespace Cratebase.Expressions;

/// <summary>Nature d'un lexème du langage de filtre.</summary>
public enum FilterTokenKind
{
    /// <summary>Fin de l'entrée.</summary>
    Eof,

    /// <summary>Chemin d'identifiant : <c>title</c>, <c>author.name</c>, <c>@request.auth.id</c>.</summary>
    Identifier,

    /// <summary>Littéral textuel, quotes déjà retirées et échappements déjà résolus.</summary>
    StringLiteral,

    /// <summary>Littéral numérique.</summary>
    Number,

    /// <summary>Mot-clé <c>true</c>.</summary>
    True,

    /// <summary>Mot-clé <c>false</c>.</summary>
    False,

    /// <summary>Mot-clé <c>null</c>.</summary>
    Null,

    /// <summary>Opérateur de comparaison, éventuellement préfixé par <c>?</c>.</summary>
    Operator,

    /// <summary>Conjonction <c>&amp;&amp;</c>.</summary>
    And,

    /// <summary>Disjonction <c>||</c>.</summary>
    Or,

    /// <summary>Parenthèse ouvrante.</summary>
    LeftParen,

    /// <summary>Parenthèse fermante.</summary>
    RightParen,

    /// <summary>Séparateur d'arguments de fonction.</summary>
    Comma,
}

/// <summary>
/// Lexème produit par <see cref="FilterLexer"/>.
/// </summary>
/// <param name="Kind">Nature du lexème.</param>
/// <param name="Text">Texte, déjà déquoté et déséchappé pour les chaînes.</param>
/// <param name="Position">Décalage du premier caractère dans l'entrée, pour les messages d'erreur.</param>
public readonly record struct FilterToken(FilterTokenKind Kind, string Text, int Position)
{
    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        FilterTokenKind.Eof => "la fin de l'expression",
        FilterTokenKind.StringLiteral => $"la chaîne « {Text} »",
        _ => $"« {Text} »",
    };
}
