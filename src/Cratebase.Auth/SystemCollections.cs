using Cratebase.Core;
using Cratebase.Schema;

namespace Cratebase.Auth;

/// <summary>
/// Collections the engine sets up itself on first start.
/// </summary>
/// <remarks>
/// Their identifiers are deterministic, for the same reason as system fields: a migration
/// snapshot produced on one machine must designate the same objects on another.
/// </remarks>
public static class SystemCollections
{
    /// <summary>Creates missing system collections.</summary>
    public static async Task EnsureAsync(
        CollectionRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (registry.Find(AuthService.RolesCollection) is null)
        {
            await registry.CreateAsync(Roles(), cancellationToken).ConfigureAwait(false);
        }

        if (registry.Find(AuthService.SuperusersCollection) is null)
        {
            await registry.CreateAsync(Superusers(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Definition of the superusers collection.</summary>
    public static CollectionDefinition Superusers() => new()
    {
        Id = Deterministic(1),
        Name = AuthService.SuperusersCollection,
        Kind = CollectionKind.Auth,
        IsSystem = true,

        // Every rule locked: only a superuser reaches this collection, which is how they
        // administer their peers from the console. Secrets are protected not by the rule but by
        // hidden fields — "password" and "tokenKey" leave no route, superusers included.
        Rules = AccessRules.Locked,
        Fields = [],
    };

    /// <summary>Definition of the roles collection.</summary>
    public static CollectionDefinition Roles() => new()
    {
        Id = Deterministic(2),
        Name = AuthService.RolesCollection,
        Kind = CollectionKind.Base,
        IsSystem = true,
        Rules = AccessRules.Locked,
        Fields =
        [
            new FieldDefinition
            {
                Id = Deterministic(10),
                Name = "name",
                Type = FieldType.Text,
                Required = true,
            },
            new FieldDefinition
            {
                Id = Deterministic(11),
                Name = AuthService.RoleGrantsField,
                Type = FieldType.Text,
                MaxSelect = 200,
            },
        ],
        Indexes = [new CollectionIndex("idx_roles_name", ["name"], Unique: true)],
    };

    private static RecordId Deterministic(byte ordinal)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        bytes[0] = 0xCB;
        bytes[15] = ordinal;

        return new RecordId(new Guid(bytes));
    }
}
