namespace Cratebase.Core;

/// <summary>
/// Logical types of a collection field.
/// </summary>
/// <remarks>
/// The logical type is the authority: it decides validation, normalization, and the operators
/// allowed. The physical column is only a translation, specific to each dialect, and no code
/// outside <c>Cratebase.Data.*</c> should depend on it.
/// </remarks>
public enum FieldType
{
    /// <summary>Free text. Options: length, pattern, autogeneration.</summary>
    Text = 1,

    /// <summary>Rich HTML text, sanitized on write.</summary>
    Editor = 2,

    /// <summary>Number. Option: restricted to integers.</summary>
    Number = 3,

    /// <summary>Boolean. Always stored as 0/1 in SQLite — never "true"/"false".</summary>
    Bool = 4,

    /// <summary>Email address. Case-insensitive comparison.</summary>
    Email = 5,

    /// <summary>Absolute URL.</summary>
    Url = 6,

    /// <summary>Instant. Always normalized to UTC by <see cref="Timestamp"/>.</summary>
    Date = 7,

    /// <summary>Instant set by the engine on create and/or on update.</summary>
    AutoDate = 8,

    /// <summary>Value(s) taken from a closed list.</summary>
    Select = 9,

    /// <summary>File(s). The column only carries the name; the bytes live in the <c>IObjectStore</c>.</summary>
    File = 10,

    /// <summary>Reference(s) to another collection.</summary>
    Relation = 11,

    /// <summary>Arbitrary JSON document. The only type that admits <c>null</c> as its own value.</summary>
    Json = 12,

    /// <summary>Geographic point (longitude, latitude).</summary>
    GeoPoint = 13,
}

/// <summary>
/// Characteristics of a logical type, independent of the dialect.
/// </summary>
public static class FieldTypeInfo
{
    /// <summary>
    /// Indicates whether the type can carry several values (per the field's <c>MaxSelect</c> option).
    /// </summary>
    /// <remarks>
    /// <see cref="FieldType.Text"/> is included, unlike PocketBase. That is what allows a list of
    /// free-form strings — permissions, tags — without going through a closed list. Storage and the
    /// compiler handle the "multiple" case generically, so including it costs nothing; omitting it,
    /// on the other hand, is expensive: an array placed on a field presumed scalar used to be stored
    /// exactly as <c>ToString()</c> renders it, i.e. <c>"System.String[]"</c>.
    /// </remarks>
    public static bool SupportsMultiple(this FieldType type) => type is
        FieldType.Text or FieldType.Select or FieldType.File or FieldType.Relation;

    /// <summary>
    /// Indicates whether the type compares temporally. These fields must go through
    /// <see cref="Timestamp.Normalize(DateTimeOffset)"/> before reaching storage.
    /// </summary>
    public static bool IsTemporal(this FieldType type) => type is
        FieldType.Date or FieldType.AutoDate;

    /// <summary>
    /// Indicates whether the type compares as text, and therefore admits the <c>~</c> and
    /// <c>!~</c> operators as well as the <c>:lower</c> modifier.
    /// </summary>
    public static bool IsTextual(this FieldType type) => type is
        FieldType.Text or FieldType.Editor or FieldType.Email or FieldType.Url or FieldType.Select;

    /// <summary>
    /// Default value of an unset field. No type other than <see cref="FieldType.Json"/> is
    /// nullable: this is PocketBase's rule, kept because it removes the "absent" vs. "empty"
    /// distinction that produces ambiguous filters.
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
/// Geographic point. Longitude first, as in GeoJSON.
/// </summary>
public readonly record struct GeoPoint(double Longitude, double Latitude);
