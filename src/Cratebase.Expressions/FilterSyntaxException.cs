using Cratebase.Core;

namespace Cratebase.Expressions;

/// <summary>
/// Unparsable filter expression, or one referencing an unknown field.
/// </summary>
/// <remarks>
/// Produces a 400. Deliberately a client error rather than a server error: an invalid expression
/// always comes from the client, and the message carries the position so it can be corrected.
/// </remarks>
public sealed class FilterSyntaxException(string message, int position)
    : CratebaseException(BuildMessage(message, position))
{
    /// <summary>Offset of the offending character in the original expression.</summary>
    public int Position { get; } = position;

    /// <inheritdoc />
    public override int StatusCode => 400;

    private static string BuildMessage(string message, int position) =>
        $"Invalid filter at position {position}: {message}";
}
