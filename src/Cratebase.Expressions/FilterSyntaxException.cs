using Cratebase.Core;

namespace Cratebase.Expressions;

/// <summary>
/// Expression de filtre non analysable, ou faisant référence à un champ inconnu.
/// </summary>
/// <remarks>
/// Produit un 400. C'est volontairement une erreur d'entrée et non une erreur serveur : une
/// expression invalide vient toujours du client, et le message porte la position pour qu'il puisse
/// se corriger.
/// </remarks>
public sealed class FilterSyntaxException(string message, int position)
    : CratebaseException(BuildMessage(message, position))
{
    /// <summary>Décalage du caractère fautif dans l'expression d'origine.</summary>
    public int Position { get; } = position;

    /// <inheritdoc />
    public override int StatusCode => 400;

    private static string BuildMessage(string message, int position) =>
        $"Filtre invalide en position {position} : {message}";
}
