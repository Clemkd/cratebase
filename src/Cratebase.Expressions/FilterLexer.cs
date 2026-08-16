using System.Text;

namespace Cratebase.Expressions;

/// <summary>
/// Lexical analysis of the filter language.
/// </summary>
/// <remarks>
/// The lexer knows nothing about fields, the schema, or SQL. It only produces tokens. This is what
/// lets the same language serve both the <c>?filter=</c> parameter and collection access rules,
/// without either contaminating the other.
/// </remarks>
public sealed class FilterLexer(string input)
{
    private readonly string _input = input ?? throw new ArgumentNullException(nameof(input));
    private int _position;

    /// <summary>
    /// Splits the input into tokens, terminated by an <see cref="FilterTokenKind.Eof"/>.
    /// </summary>
    /// <exception cref="FilterSyntaxException">Unexpected character or unterminated string.</exception>
    public IReadOnlyList<FilterToken> Tokenize()
    {
        var tokens = new List<FilterToken>();

        while (true)
        {
            SkipTriviaAndComments();

            if (_position >= _input.Length)
            {
                tokens.Add(new FilterToken(FilterTokenKind.Eof, string.Empty, _position));
                return tokens;
            }

            tokens.Add(NextToken());
        }
    }

    private FilterToken NextToken()
    {
        var start = _position;
        var current = _input[_position];

        switch (current)
        {
            case '(':
                _position++;
                return new FilterToken(FilterTokenKind.LeftParen, "(", start);

            case ')':
                _position++;
                return new FilterToken(FilterTokenKind.RightParen, ")", start);

            case ',':
                _position++;
                return new FilterToken(FilterTokenKind.Comma, ",", start);

            case '\'':
            case '"':
                return ReadString(current);

            case '&':
                return ReadRepeated('&', FilterTokenKind.And, "&&");

            case '|':
                return ReadRepeated('|', FilterTokenKind.Or, "||");
        }

        if (IsOperatorStart(current))
        {
            return ReadOperator();
        }

        if (char.IsAsciiDigit(current) || (current == '-' && IsDigitAt(_position + 1)))
        {
            return ReadNumber();
        }

        if (IsIdentifierStart(current))
        {
            return ReadIdentifierOrKeyword();
        }

        throw new FilterSyntaxException($"unexpected character \"{current}\"", start);
    }

    private void SkipTriviaAndComments()
    {
        while (_position < _input.Length)
        {
            if (char.IsWhiteSpace(_input[_position]))
            {
                _position++;
                continue;
            }

            // End-of-line comment, as in PocketBase: useful in access rules, which are read far
            // more often than they are written.
            if (_input[_position] == '/' && _position + 1 < _input.Length && _input[_position + 1] == '/')
            {
                while (_position < _input.Length && _input[_position] is not ('\n' or '\r'))
                {
                    _position++;
                }

                continue;
            }

            return;
        }
    }

    private FilterToken ReadRepeated(char symbol, FilterTokenKind kind, string text)
    {
        var start = _position;

        if (_position + 1 >= _input.Length || _input[_position + 1] != symbol)
        {
            throw new FilterSyntaxException($"lone \"{symbol}\" — expected \"{text}\"", start);
        }

        _position += 2;
        return new FilterToken(kind, text, start);
    }

    private FilterToken ReadString(char quote)
    {
        var start = _position;
        _position++; // opening quote

        var value = new StringBuilder();

        while (_position < _input.Length)
        {
            var current = _input[_position];

            if (current == '\\' && _position + 1 < _input.Length)
            {
                var escaped = _input[_position + 1];
                value.Append(escaped switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => escaped,
                });
                _position += 2;
                continue;
            }

            if (current == quote)
            {
                _position++;
                return new FilterToken(FilterTokenKind.StringLiteral, value.ToString(), start);
            }

            value.Append(current);
            _position++;
        }

        throw new FilterSyntaxException("unterminated string", start);
    }

    private FilterToken ReadNumber()
    {
        var start = _position;

        if (_input[_position] == '-')
        {
            _position++;
        }

        var seenDot = false;

        while (_position < _input.Length)
        {
            var current = _input[_position];

            if (char.IsAsciiDigit(current))
            {
                _position++;
                continue;
            }

            if (current == '.' && !seenDot && IsDigitAt(_position + 1))
            {
                seenDot = true;
                _position++;
                continue;
            }

            break;
        }

        return new FilterToken(FilterTokenKind.Number, _input[start.._position], start);
    }

    private FilterToken ReadIdentifierOrKeyword()
    {
        var start = _position;

        while (_position < _input.Length && IsIdentifierPart(_input[_position]))
        {
            _position++;
        }

        var text = _input[start.._position];

        var kind = text switch
        {
            "true" => FilterTokenKind.True,
            "false" => FilterTokenKind.False,
            "null" => FilterTokenKind.Null,
            _ => FilterTokenKind.Identifier,
        };

        return new FilterToken(kind, text, start);
    }

    private FilterToken ReadOperator()
    {
        var start = _position;
        var anyOf = _input[_position] == '?';

        if (anyOf)
        {
            _position++;

            if (_position >= _input.Length || !IsOperatorStart(_input[_position]) || _input[_position] == '?')
            {
                throw new FilterSyntaxException("\"?\" must prefix a comparison operator", start);
            }
        }

        var symbol = ReadOperatorSymbol(start);

        return new FilterToken(FilterTokenKind.Operator, anyOf ? "?" + symbol : symbol, start);
    }

    private string ReadOperatorSymbol(int start)
    {
        var current = _input[_position];
        var next = _position + 1 < _input.Length ? _input[_position + 1] : '\0';

        switch (current)
        {
            case '=':
                _position++;
                return "=";

            case '!' when next is '=' or '~':
                _position += 2;
                return next == '=' ? "!=" : "!~";

            case '~':
                _position++;
                return "~";

            case '>' or '<':
                _position++;

                if (next == '=')
                {
                    _position++;
                    return current == '>' ? ">=" : "<=";
                }

                return current == '>' ? ">" : "<";

            default:
                throw new FilterSyntaxException($"unknown operator \"{current}\"", start);
        }
    }

    private bool IsDigitAt(int index) => index < _input.Length && char.IsAsciiDigit(_input[index]);

    private static bool IsOperatorStart(char c) => c is '=' or '!' or '>' or '<' or '~' or '?';

    private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c is '_' or '@';

    // The dot separates a path's segments, the colon introduces a modifier (:isset, :length,
    // :each, :lower, :changed). Both belong to the token: the parser breaks them apart once it
    // knows it is indeed holding an identifier.
    private static bool IsIdentifierPart(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '@' or '.' or ':';
}
