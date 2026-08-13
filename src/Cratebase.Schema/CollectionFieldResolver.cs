using Cratebase.Data;

namespace Cratebase.Schema;

/// <summary>
/// Résout les chemins d'un filtre contre le schéma d'une collection.
/// </summary>
/// <remarks>
/// C'est l'implémentation concrète de la liste blanche du compilateur. Elle ne connaît que les
/// champs déclarés : tout le reste est refusé, et le refus produit une erreur 400 avant qu'aucun
/// SQL ne soit écrit.
/// </remarks>
public sealed class CollectionFieldResolver(CollectionDefinition collection) : IQueryFieldResolver
{
    private readonly CollectionDefinition _collection = collection
        ?? throw new ArgumentNullException(nameof(collection));

    /// <inheritdoc />
    public string RootCollection => _collection.Name;

    /// <inheritdoc />
    public bool TryResolve(IReadOnlyList<string> segments, out ResolvedField field)
    {
        ArgumentNullException.ThrowIfNull(segments);

        field = null!;

        // La traversée de relations (« author.name ») demande une jointure, donc une réécriture de
        // la requête et un contrôle d'accès supplémentaire sur la collection traversée. Tant que ce
        // n'est pas écrit, on refuse explicitement plutôt que de résoudre le premier segment et de
        // produire une requête au sens différent de ce qui a été demandé.
        if (segments.Count != 1)
        {
            return false;
        }

        var definition = _collection.Field(segments[0]);

        if (definition is null || definition.Hidden)
        {
            // Un champ masqué (mot de passe, clé de jeton) est traité comme inexistant : le rendre
            // filtrable permettrait de deviner un secret caractère par caractère avec « ~ ».
            return false;
        }

        field = new ResolvedField(
            definition.ColumnName,
            definition.Type,
            definition.Multiple,
            definition.Options.TargetCollection);

        return true;
    }
}
