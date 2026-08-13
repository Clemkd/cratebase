namespace Cratebase.Core;

/// <summary>
/// Types logiques d'un champ de collection.
/// </summary>
/// <remarks>
/// Le type logique fait autorité : c'est lui qui décide de la validation, de la normalisation et
/// des opérateurs admis. La colonne physique n'est qu'une traduction, propre à chaque dialecte, et
/// aucun code hors de <c>Cratebase.Data.*</c> ne doit en dépendre.
/// </remarks>
public enum FieldType
{
    /// <summary>Texte libre. Options : longueur, motif, génération automatique.</summary>
    Text = 1,

    /// <summary>Texte enrichi HTML, assaini à l'écriture.</summary>
    Editor = 2,

    /// <summary>Nombre. Option : restreint aux entiers.</summary>
    Number = 3,

    /// <summary>Booléen. Toujours stocké 0/1 en SQLite — jamais « true »/« false ».</summary>
    Bool = 4,

    /// <summary>Adresse de courriel. Comparaison insensible à la casse.</summary>
    Email = 5,

    /// <summary>URL absolue.</summary>
    Url = 6,

    /// <summary>Instant. Toujours normalisé en UTC par <see cref="Timestamp"/>.</summary>
    Date = 7,

    /// <summary>Instant renseigné par le moteur à la création et/ou à la mise à jour.</summary>
    AutoDate = 8,

    /// <summary>Valeur(s) prise(s) dans une liste fermée.</summary>
    Select = 9,

    /// <summary>Fichier(s). La colonne ne porte que le nom, l'octet vit dans l'<c>IObjectStore</c>.</summary>
    File = 10,

    /// <summary>Référence(s) vers une autre collection.</summary>
    Relation = 11,

    /// <summary>Document JSON arbitraire. Seul type admettant <c>null</c> comme valeur propre.</summary>
    Json = 12,

    /// <summary>Point géographique (longitude, latitude).</summary>
    GeoPoint = 13,
}

/// <summary>
/// Caractéristiques d'un type logique, indépendantes du dialecte.
/// </summary>
public static class FieldTypeInfo
{
    /// <summary>
    /// Indique si le type peut porter plusieurs valeurs (selon l'option <c>MaxSelect</c> du champ).
    /// </summary>
    /// <remarks>
    /// <see cref="FieldType.Text"/> en fait partie, contrairement à PocketBase. C'est ce qui permet
    /// une liste de chaînes libres — permissions, étiquettes — sans passer par une liste fermée. Le
    /// stockage et le compilateur traitent le cas « multiple » de façon générique, donc l'ajout ne
    /// coûte rien ; l'omettre, en revanche, se paie cher : un tableau posé sur un champ réputé
    /// simple était stocké tel que <c>ToString()</c> le rend, soit <c>« System.String[] »</c>.
    /// </remarks>
    public static bool SupportsMultiple(this FieldType type) => type is
        FieldType.Text or FieldType.Select or FieldType.File or FieldType.Relation;

    /// <summary>
    /// Indique si le type se compare temporellement. Ces champs passent obligatoirement par
    /// <see cref="Timestamp.Normalize(DateTimeOffset)"/> avant d'atteindre le stockage.
    /// </summary>
    public static bool IsTemporal(this FieldType type) => type is
        FieldType.Date or FieldType.AutoDate;

    /// <summary>
    /// Indique si le type se compare comme du texte, donc s'il admet les opérateurs <c>~</c> et
    /// <c>!~</c> ainsi que le modificateur <c>:lower</c>.
    /// </summary>
    public static bool IsTextual(this FieldType type) => type is
        FieldType.Text or FieldType.Editor or FieldType.Email or FieldType.Url or FieldType.Select;

    /// <summary>
    /// Valeur par défaut d'un champ non renseigné. Aucun type sauf <see cref="FieldType.Json"/>
    /// n'est nullable : c'est la règle de PocketBase, reprise parce qu'elle supprime la distinction
    /// « absent » / « vide » qui produit des filtres ambigus.
    /// </summary>
    public static object? ZeroValue(this FieldType type, bool multiple) => (type, multiple) switch
    {
        (FieldType.Json, _) => null,
        (_, true) => Array.Empty<string>(),
        (FieldType.Bool, _) => false,
        (FieldType.Number, _) => 0d,
        (FieldType.GeoPoint, _) => new GeoPoint(0, 0),
        _ => string.Empty,
    };
}

/// <summary>
/// Point géographique. Longitude d'abord, comme en GeoJSON.
/// </summary>
public readonly record struct GeoPoint(double Longitude, double Latitude);
