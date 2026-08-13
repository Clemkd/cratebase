using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Schema;

namespace Cratebase.Records;

/// <summary>
/// Paramètres d'une requête de liste.
/// </summary>
public sealed record RecordQuery
{
    /// <summary>Taille de page par défaut, comme chez PocketBase.</summary>
    public const int DefaultPerPage = 30;

    /// <summary>
    /// Taille de page maximale.
    /// </summary>
    /// <remarks>
    /// Borne obligatoire : sans elle, <c>?perPage=1000000</c> est un déni de service en une URL.
    /// Le plafonnement est silencieux plutôt qu'une erreur — c'est le comportement de PocketBase,
    /// et il évite de casser un client qui demanderait un peu trop.
    /// </remarks>
    public const int MaxPerPage = 500;

    /// <summary>Page demandée, à partir de 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Taille de page demandée.</summary>
    public int PerPage { get; init; } = DefaultPerPage;

    /// <summary>Expression de filtre.</summary>
    public string? Filter { get; init; }

    /// <summary>Tri, champs séparés par des virgules, préfixés de <c>-</c> pour l'ordre décroissant.</summary>
    public string? Sort { get; init; }

    /// <summary>Projection des champs renvoyés.</summary>
    public string? Fields { get; init; }

    /// <summary>Omettre le décompte total, qui coûte une requête supplémentaire.</summary>
    public bool SkipTotal { get; init; }

    /// <summary>Page effective, bornée.</summary>
    public int EffectivePage => Math.Max(1, Page);

    /// <summary>Taille de page effective, bornée.</summary>
    public int EffectivePerPage => Math.Clamp(PerPage, 1, MaxPerPage);

    /// <summary>Décalage correspondant.</summary>
    public int Offset => (EffectivePage - 1) * EffectivePerPage;
}

/// <summary>
/// Compilation de la clause de tri.
/// </summary>
public static class SortCompiler
{
    /// <summary>Nombre maximal de champs de tri.</summary>
    public const int MaxSortFields = 8;

    /// <summary>
    /// Compile une expression de tri en clause <c>ORDER BY</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ Chaque champ passe par le résolveur, exactement comme dans un filtre. Un tri non contrôlé
    /// est une fuite discrète : trier sur un champ qu'on n'a pas le droit de lire ne l'affiche pas,
    /// mais <b>l'ordre des résultats en révèle les valeurs</b>. Un champ masqué est donc refusé au
    /// tri comme il l'est au filtre.
    /// </remarks>
    public static string Compile(
        string? sort,
        CollectionDefinition collection,
        ISqlDialect dialect,
        string alias)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(dialect);

        // Ordre par défaut : le plus récent d'abord. Un ORDER BY est indispensable — sans lui, la
        // pagination n'a pas de sens, et les deux moteurs ne rendent pas les lignes dans le même
        // ordre.
        if (string.IsNullOrWhiteSpace(sort))
        {
            return $"{dialect.QuoteIdentifier(alias)}.{dialect.QuoteIdentifier(SystemFields.Id)} DESC";
        }

        var terms = new List<string>();

        foreach (var raw in sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (terms.Count >= MaxSortFields)
            {
                throw new CratebaseBadRequestException(
                    $"Le tri ne peut porter sur plus de {MaxSortFields} champs.");
            }

            var descending = raw[0] == '-';
            var name = raw[0] is '-' or '+' ? raw[1..] : raw;

            var field = collection.Field(name);

            if (field is null || field.Hidden)
            {
                throw new CratebaseBadRequestException(
                    $"« {name} » n'est pas un champ triable de la collection « {collection.Name} ».");
            }

            var expression = $"{dialect.QuoteIdentifier(alias)}.{dialect.QuoteIdentifier(field.ColumnName)}";

            terms.Add(dialect.OrderByNullsLast(expression, descending));
        }

        // Départage stable : sans dernier critère déterministe, deux lignes de même valeur peuvent
        // changer d'ordre entre deux pages et l'une d'elles n'apparaît jamais.
        terms.Add($"{dialect.QuoteIdentifier(alias)}.{dialect.QuoteIdentifier(SystemFields.Id)} DESC");

        return string.Join(", ", terms);
    }
}
