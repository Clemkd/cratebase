using Cratebase.Core;
using Cratebase.Schema;

namespace Cratebase.Auth;

/// <summary>
/// Collections que le moteur pose lui-même au premier démarrage.
/// </summary>
/// <remarks>
/// Leurs identifiants sont déterministes, pour la même raison que ceux des champs système : un
/// instantané de migration produit sur une machine doit désigner les mêmes objets sur une autre.
/// </remarks>
public static class SystemCollections
{
    /// <summary>Crée les collections système manquantes.</summary>
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

    /// <summary>Définition de la collection des super-admins.</summary>
    public static CollectionDefinition Superusers() => new()
    {
        Id = Deterministic(1),
        Name = AuthService.SuperusersCollection,
        Kind = CollectionKind.Auth,
        IsSystem = true,

        // Toutes les règles verrouillées : seul un super-admin atteint cette collection,
        // ce qui lui permet d'administrer ses pairs depuis la console. Les secrets ne sont pas
        // protégés par la règle mais par les champs masqués — « password » et « tokenKey » ne
        // sortent d'aucune route, super-admin compris.
        Rules = AccessRules.Locked,
        Fields = [],
    };

    /// <summary>Définition de la collection des rôles.</summary>
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
