namespace Cratebase.Expressions;

/// <summary>Kind of a filter language token.</summary>
public enum FilterTokenKind
{
    /// <summary>End of input.</summary>
    Eof,

    /// <summary>Identifier path: <c>title</c>, <c>author.name</c>, <c>@request.auth.id</c>.</summary>
    Identifier,

    /// <summary>String literal, quotes already stripped and escapes already resolved.</summary>
    StringLiteral,

    /// <summary>Numeric literal.</summary>
    Number,

    /// <summary>Keyword <c>true</c>.</summary>
    True,

    /// <summary>Keyword <c>false</c>.</summary>
    False,

    /// <summary>Keyword <c>null</c>.</summary>
    Null,

    /// <summary>Comparison operator, optionally prefixed with <c>?</c>.</summary>
    Operator,

    /// <summary>Conjunction <c>&amp;&amp;</c>.</summary>
    And,

    /// <summary>Disjunction <c>||</c>.</summary>
    Or,

    /// <summary>Opening parenthesis.</summary>
    LeftParen,

    /// <summary>Closing parenthesis.</summary>
    RightParen,

    /// <summary>Function argument separator.</summary>
    Comma,
}

/// <summary>
/// Token produced by <see cref="FilterLexer"/>.
/// </summary>
/// <param name="Kind">Kind of the token.</param>
/// <param name="Text">Text, already unquoted and unescaped for strings.</param>
/// <param name="Position">Offset of the first character in the input, for error messages.</param>
public readonly record struct FilterToken(FilterTokenKind Kind, string Text, int Position)
{
    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        FilterTokenKind.Eof => "end of expression",
        FilterTokenKind.StringLiteral => $"the string \"{Text}\"",
        _ => $"\"{Text}\"",
    };
}
