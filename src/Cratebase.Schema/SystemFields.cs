using Cratebase.Core;

namespace Cratebase.Schema;

/// <summary>
/// Fields the engine itself places on every collection.
/// </summary>
/// <remarks>
/// <para>
/// Their identifiers are <b>deterministic</b>, not random: they must be identical on every
/// installation, otherwise a migration snapshot produced on one machine would rename the system
/// columns on another.
/// </para>
/// <para>
/// Their names are in <b>English snake_case</b>, like base columns rather than JavaScript
/// properties. That's what someone opening the database with another tool sees, and a convention
/// that changes depending on whether a field is a system field or not forces remembering which
/// applies where. Renaming a system field is picked up automatically at startup: since the
/// identifiers are stable, the planner sees a rename and preserves the data.
/// </para>
/// </remarks>
public static class SystemFields
{
    /// <summary>Name of the identifier field.</summary>
    public const string Id = "id";

    /// <summary>Name of the creation-date field.</summary>
    public const string Created = "created";

    /// <summary>Name of the modification-date field.</summary>
    public const string Updated = "updated";

    /// <summary>Name of the email address field on auth collections.</summary>
    public const string Email = "email";

    /// <summary>Name of the address-visibility field.</summary>
    public const string EmailVisibility = "email_visibility";

    /// <summary>Name of the verified-address field.</summary>
    public const string Verified = "verified";

    /// <summary>Name of the password field. Never returned by the API.</summary>
    public const string Password = "password";

    /// <summary>Name of the token-key field. Never returned by the API.</summary>
    public const string TokenKey = "token_key";

    /// <summary>Name of the field carrying an account's roles.</summary>
    public const string Roles = "roles";

    /// <summary>Name of the field carrying permissions granted directly to an account.</summary>
    public const string Permissions = "permissions";

    /// <summary>
    /// Password confirmation field: accepted on input, never stored.
    /// </summary>
    /// <remarks>
    /// It is not a column, so validation would otherwise reject it as an unknown field. It's
    /// listed here to be explicitly tolerated — the tolerance is declared, not left as a special
    /// case buried in the validator.
    /// </remarks>
    public const string PasswordConfirm = "password_confirm";

    /// <summary>
    /// Fields accepted in a request body without corresponding to a column.
    /// </summary>
    public static IReadOnlySet<string> InputOnlyFields { get; } =
        new HashSet<string>(StringComparer.Ordinal) { PasswordConfirm };

    /// <summary>Fields present on every collection.</summary>
    public static IReadOnlyList<FieldDefinition> ForBase() =>
    [
        new FieldDefinition
        {
            Id = Deterministic(1),
            Name = Id,
            Type = FieldType.Text,
            Required = true,
            IsSystem = true,
        },
        new FieldDefinition
        {
            Id = Deterministic(2),
            Name = Created,
            Type = FieldType.AutoDate,
            IsSystem = true,
            Options = new FieldOptions { OnCreate = true },
        },
        new FieldDefinition
        {
            Id = Deterministic(3),
            Name = Updated,
            Type = FieldType.AutoDate,
            IsSystem = true,
            Options = new FieldOptions { OnCreate = true, OnUpdate = true },
        },
    ];

    /// <summary>Extra fields of an authentication collection.</summary>
    public static IReadOnlyList<FieldDefinition> ForAuth() =>
    [
        .. ForBase(),
        new FieldDefinition
        {
            Id = Deterministic(4),
            Name = Email,
            Type = FieldType.Email,
            Required = true,
            IsSystem = true,
        },
        new FieldDefinition
        {
            Id = Deterministic(5),
            Name = EmailVisibility,
            Type = FieldType.Bool,
            IsSystem = true,
        },
        new FieldDefinition
        {
            Id = Deterministic(6),
            Name = Verified,
            Type = FieldType.Bool,
            IsSystem = true,
        },
        new FieldDefinition
        {
            Id = Deterministic(7),
            Name = Password,
            Type = FieldType.Text,
            Required = true,
            IsSystem = true,
            Hidden = true,
        },
        new FieldDefinition
        {
            Id = Deterministic(8),
            Name = TokenKey,
            Type = FieldType.Text,
            Required = true,
            IsSystem = true,
            Hidden = true,
        },

        // Roles and permissions are SYSTEM fields, so they're rejected in an ordinary request
        // body. Making them writable through the records API would let an account grant itself
        // rights by POSTing "permissions: ['*']" — as soon as the create rule is open, which is
        // the most common case on sign-up. Assignment therefore goes through a superuser-only
        // endpoint.
        new FieldDefinition
        {
            Id = Deterministic(9),
            Name = Roles,
            Type = FieldType.Text,
            IsSystem = true,
            MaxSelect = 20,
        },
        new FieldDefinition
        {
            Id = Deterministic(10),
            Name = Permissions,
            Type = FieldType.Text,
            IsSystem = true,
            MaxSelect = 100,
        },
    ];

    /// <summary>Indexes required on an authentication collection.</summary>
    public static IReadOnlyList<CollectionIndex> AuthIndexes(string collectionName) =>
    [
        new CollectionIndex($"idx_{collectionName}_email", [Email], Unique: true),
        new CollectionIndex($"idx_{collectionName}_token_key", [TokenKey], Unique: true),
    ];

    /// <summary>Is the field a system field?</summary>
    public static bool IsSystemField(string name) => name is
        Id or Created or Updated or Email or EmailVisibility or Verified or Password or TokenKey
        or Roles or Permissions;

    private static RecordId Deterministic(byte ordinal)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        bytes[15] = ordinal;

        return new RecordId(new Guid(bytes));
    }
}
