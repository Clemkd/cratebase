using Cratebase.Core;

namespace Cratebase.Schema;

/// <summary>Nature of a collection.</summary>
public enum CollectionKind
{
    /// <summary>Ordinary data collection.</summary>
    Base = 1,

    /// <summary>
    /// Authentication collection: carries accounts, and exposes login endpoints. Several can
    /// coexist — end users and client administrators, for instance.
    /// </summary>
    Auth = 2,

    /// <summary>
    /// Read-only collection, fed by a query.
    /// </summary>
    /// <remarks>
    /// ⚠️ Unlike PocketBase, the query is <b>not</b> raw SQL: it's a query expressed in Cratebase's
    /// own language. Allowing SQL would make the collection unportable between engines, which
    /// would contradict the project's whole reason for existing.
    /// </remarks>
    View = 3,
}

/// <summary>
/// Access rules of a collection.
/// </summary>
/// <remarks>
/// <para>
/// Three states per rule, carried over from PocketBase and structural:
/// </para>
/// <list type="bullet">
/// <item><see langword="null"/> — <b>locked</b>: superuser only. This is the default.</item>
/// <item>empty string — open to everyone, including anonymous visitors.</item>
/// <item>expression — allowed if the expression is true.</item>
/// </list>
/// <para>
/// Conflating <see langword="null"/> and the empty string would open every collection in the
/// system. This is why the type is <c>string?</c> rather than <c>string</c> with a convention.
/// </para>
/// </remarks>
public sealed record AccessRules
{
    /// <summary>Every rule locked: the state of a newly created collection.</summary>
    public static readonly AccessRules Locked = new();

    /// <summary>List rule. A violation returns an empty page, never an error.</summary>
    public string? List { get; init; }

    /// <summary>View rule. A violation returns 404, so as not to disclose existence.</summary>
    public string? View { get; init; }

    /// <summary>Create rule. A violation returns 400.</summary>
    public string? Create { get; init; }

    /// <summary>Update rule. A violation returns 404.</summary>
    public string? Update { get; init; }

    /// <summary>Delete rule. A violation returns 404.</summary>
    public string? Delete { get; init; }

    /// <summary>
    /// Management rule, specific to auth collections: who can change another account's password
    /// or address. A violation returns 403.
    /// </summary>
    public string? Manage { get; init; }

    /// <summary>Returns the rule corresponding to an action.</summary>
    public string? For(CollectionAction action) => action switch
    {
        CollectionAction.List => List,
        CollectionAction.View => View,
        CollectionAction.Create => Create,
        CollectionAction.Update => Update,
        CollectionAction.Delete => Delete,
        CollectionAction.Manage => Manage,
        _ => null,
    };
}

/// <summary>Action subject to an access rule.</summary>
public enum CollectionAction
{
    /// <summary>List.</summary>
    List,

    /// <summary>View a row.</summary>
    View,

    /// <summary>Create.</summary>
    Create,

    /// <summary>Update.</summary>
    Update,

    /// <summary>Delete.</summary>
    Delete,

    /// <summary>Manage a third-party account.</summary>
    Manage,
}

/// <summary>
/// Index declared on a collection.
/// </summary>
/// <param name="Name">Index name, unique within the database.</param>
/// <param name="Fields">Indexed fields, in order.</param>
/// <param name="Unique">Does the index enforce uniqueness?</param>
public sealed record CollectionIndex(string Name, IReadOnlyList<string> Fields, bool Unique = false);

/// <summary>
/// Definition of a collection.
/// </summary>
public sealed record CollectionDefinition
{
    /// <summary>Identifier.</summary>
    public required RecordId Id { get; init; }

    /// <summary>Name. Also used as the table name, so validated by <see cref="Identifier"/>.</summary>
    public required string Name { get; init; }

    /// <summary>Nature.</summary>
    public CollectionKind Kind { get; init; } = CollectionKind.Base;

    /// <summary>Fields, system fields included.</summary>
    public IReadOnlyList<FieldDefinition> Fields { get; init; } = [];

    /// <summary>Declared indexes.</summary>
    public IReadOnlyList<CollectionIndex> Indexes { get; init; } = [];

    /// <summary>Access rules. Locked by default.</summary>
    public AccessRules Rules { get; init; } = AccessRules.Locked;

    /// <summary>Does the collection belong to the engine? A system collection is protected.</summary>
    public bool IsSystem { get; init; }

    /// <summary>Creation date.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>Last modification date.</summary>
    public DateTimeOffset Updated { get; init; }

    /// <summary>Name of the table holding the data.</summary>
    public string TableName => Name;

    /// <summary>Finds a field by its name.</summary>
    public FieldDefinition? Field(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
}
