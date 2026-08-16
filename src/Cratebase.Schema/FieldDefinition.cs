using Cratebase.Core;

namespace Cratebase.Schema;

/// <summary>
/// Options of a field. All optional, interpreted according to the type.
/// </summary>
public sealed record FieldOptions
{
    /// <summary>No option.</summary>
    public static readonly FieldOptions None = new();

    /// <summary>Minimum length or value.</summary>
    public double? Min { get; init; }

    /// <summary>Maximum length or value.</summary>
    public double? Max { get; init; }

    /// <summary>Pattern the value must match (<see cref="FieldType.Text"/> type).</summary>
    public string? Pattern { get; init; }

    /// <summary>Is the field restricted to integers (<see cref="FieldType.Number"/> type)?</summary>
    public bool IntegerOnly { get; init; }

    /// <summary>Admitted values (<see cref="FieldType.Select"/> type).</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>Target collection (<see cref="FieldType.Relation"/> type).</summary>
    public string? TargetCollection { get; init; }

    /// <summary>Does deleting the target delete the row carrying the relation?</summary>
    public bool CascadeDelete { get; init; }

    /// <summary>Maximum file size, in bytes.</summary>
    public long? MaxFileSize { get; init; }

    /// <summary>Admitted MIME types (<see cref="FieldType.File"/> type).</summary>
    public IReadOnlyList<string> MimeTypes { get; init; } = [];

    /// <summary>Thumbnail sizes to generate, in <c>WxH</c> form.</summary>
    public IReadOnlyList<string> ThumbSizes { get; init; } = [];

    /// <summary>
    /// Is the file protected? A protected file is only served to callers who satisfy the
    /// collection's view rule.
    /// </summary>
    public bool Protected { get; init; }

    /// <summary>Set on creation (<see cref="FieldType.AutoDate"/> type).</summary>
    public bool OnCreate { get; init; }

    /// <summary>Set on every modification (<see cref="FieldType.AutoDate"/> type).</summary>
    public bool OnUpdate { get; init; }
}

/// <summary>
/// Definition of a collection field.
/// </summary>
public sealed record FieldDefinition
{
    /// <summary>
    /// Stable identifier of the field.
    /// </summary>
    /// <remarks>
    /// ⚠️ Indispensable, and easy to assume unnecessary. Without an identifier, renaming a field is
    /// indistinguishable from a deletion followed by an addition: the planner would emit
    /// <c>DROP COLUMN</c> then <c>ADD COLUMN</c>, and <b>every value in that column would
    /// disappear</b> with nothing to report it. The identifier makes a rename detectable, turning
    /// the operation into a <c>RENAME COLUMN</c> that preserves the content.
    /// </remarks>
    public required RecordId Id { get; init; }

    /// <summary>Name. Also used as the column name, so validated by <see cref="Identifier"/>.</summary>
    public required string Name { get; init; }

    /// <summary>Logical type.</summary>
    public required FieldType Type { get; init; }

    /// <summary>Is the value required?</summary>
    public bool Required { get; init; }

    /// <summary>Does the field belong to the engine? A system field can be neither renamed nor deleted.</summary>
    public bool IsSystem { get; init; }

    /// <summary>
    /// Is the field hidden from responses? True for the password and the token key.
    /// </summary>
    public bool Hidden { get; init; }

    /// <summary>
    /// Maximum number of values. <c>1</c> for a scalar field, more for a multi-valued field.
    /// </summary>
    public int MaxSelect { get; init; } = 1;

    /// <summary>Options specific to the type.</summary>
    public FieldOptions Options { get; init; } = FieldOptions.None;

    /// <summary>Does the field actually carry several values?</summary>
    public bool Multiple => MaxSelect != 1 && Type.SupportsMultiple();

    /// <summary>Name of the column carrying the field.</summary>
    public string ColumnName => Name;
}
