using Cratebase.Core;

namespace Cratebase.Schema;

/// <summary>
/// Options d'un champ. Toutes facultatives, interprétées selon le type.
/// </summary>
public sealed record FieldOptions
{
    /// <summary>Aucune option.</summary>
    public static readonly FieldOptions None = new();

    /// <summary>Longueur ou valeur minimale.</summary>
    public double? Min { get; init; }

    /// <summary>Longueur ou valeur maximale.</summary>
    public double? Max { get; init; }

    /// <summary>Motif que la valeur doit respecter (type <see cref="FieldType.Text"/>).</summary>
    public string? Pattern { get; init; }

    /// <summary>Le champ est-il restreint aux entiers (type <see cref="FieldType.Number"/>) ?</summary>
    public bool IntegerOnly { get; init; }

    /// <summary>Valeurs admises (type <see cref="FieldType.Select"/>).</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>Collection visée (type <see cref="FieldType.Relation"/>).</summary>
    public string? TargetCollection { get; init; }

    /// <summary>La suppression de la cible supprime-t-elle la ligne portant la relation ?</summary>
    public bool CascadeDelete { get; init; }

    /// <summary>Taille maximale d'un fichier, en octets.</summary>
    public long? MaxFileSize { get; init; }

    /// <summary>Types MIME admis (type <see cref="FieldType.File"/>).</summary>
    public IReadOnlyList<string> MimeTypes { get; init; } = [];

    /// <summary>Tailles de vignettes à générer, au format <c>LxH</c>.</summary>
    public IReadOnlyList<string> ThumbSizes { get; init; } = [];

    /// <summary>
    /// Le fichier est-il protégé ? Un fichier protégé n'est servi qu'aux appelants qui satisfont la
    /// règle de consultation de la collection.
    /// </summary>
    public bool Protected { get; init; }

    /// <summary>Renseigner à la création (type <see cref="FieldType.AutoDate"/>).</summary>
    public bool OnCreate { get; init; }

    /// <summary>Renseigner à chaque modification (type <see cref="FieldType.AutoDate"/>).</summary>
    public bool OnUpdate { get; init; }
}

/// <summary>
/// Définition d'un champ de collection.
/// </summary>
public sealed record FieldDefinition
{
    /// <summary>
    /// Identifiant stable du champ.
    /// </summary>
    /// <remarks>
    /// ⚠️ Indispensable, et facile à croire superflu. Sans identifiant, un renommage de champ est
    /// indiscernable d'une suppression suivie d'un ajout : le planificateur émettrait
    /// <c>DROP COLUMN</c> puis <c>ADD COLUMN</c>, et <b>toutes les données de la colonne
    /// disparaîtraient</b> sans que rien ne le signale. L'identifiant rend le renommage détectable,
    /// donc l'opération devient un <c>RENAME COLUMN</c> qui préserve le contenu.
    /// </remarks>
    public required RecordId Id { get; init; }

    /// <summary>Nom. Sert aussi de nom de colonne, donc validé par <see cref="Identifier"/>.</summary>
    public required string Name { get; init; }

    /// <summary>Type logique.</summary>
    public required FieldType Type { get; init; }

    /// <summary>La valeur est-elle obligatoire ?</summary>
    public bool Required { get; init; }

    /// <summary>Le champ appartient-il au moteur ? Un champ système ne peut être ni renommé ni supprimé.</summary>
    public bool IsSystem { get; init; }

    /// <summary>
    /// Le champ est-il masqué dans les réponses ? Vrai pour le mot de passe et la clé de jeton.
    /// </summary>
    public bool Hidden { get; init; }

    /// <summary>
    /// Nombre maximal de valeurs. <c>1</c> pour un champ simple, davantage pour un champ multiple.
    /// </summary>
    public int MaxSelect { get; init; } = 1;

    /// <summary>Options propres au type.</summary>
    public FieldOptions Options { get; init; } = FieldOptions.None;

    /// <summary>Le champ porte-t-il effectivement plusieurs valeurs ?</summary>
    public bool Multiple => MaxSelect != 1 && Type.SupportsMultiple();

    /// <summary>Nom de la colonne portant le champ.</summary>
    public string ColumnName => Name;
}
