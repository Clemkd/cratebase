using Cratebase.Core;

namespace Cratebase.Data;

/// <summary>
/// Champ résolu contre le schéma d'une collection.
/// </summary>
/// <param name="ColumnName">Nom physique de la colonne.</param>
/// <param name="Type">Type logique.</param>
/// <param name="Multiple">Le champ porte-t-il plusieurs valeurs ?</param>
/// <param name="TargetCollection">Collection visée, pour un champ de type relation.</param>
public sealed record ResolvedField(
    string ColumnName,
    FieldType Type,
    bool Multiple,
    string? TargetCollection = null);

/// <summary>
/// Résolution d'un chemin d'identifiant contre le schéma.
/// </summary>
/// <remarks>
/// <b>C'est la liste blanche.</b> Un chemin que le résolveur refuse ne doit jamais atteindre le
/// SQL : le compilateur lève une erreur 400. C'est ce qui rend l'injection impossible par
/// construction plutôt que par vigilance — un champ inventé par un client n'a aucun moyen de
/// devenir du texte SQL.
/// </remarks>
public interface IQueryFieldResolver
{
    /// <summary>Nom de la collection racine.</summary>
    string RootCollection { get; }

    /// <summary>
    /// Résout un chemin relatif à la collection racine.
    /// </summary>
    /// <param name="segments">Segments du chemin, par exemple <c>["author", "name"]</c>.</param>
    /// <param name="field">Champ résolu, si le chemin est valide.</param>
    /// <returns><see langword="false"/> si le chemin ne désigne aucun champ connu.</returns>
    bool TryResolve(IReadOnlyList<string> segments, out ResolvedField field);
}
