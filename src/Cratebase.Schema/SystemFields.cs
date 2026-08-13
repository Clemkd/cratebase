using Cratebase.Core;

namespace Cratebase.Schema;

/// <summary>
/// Champs que le moteur pose lui-même sur toute collection.
/// </summary>
/// <remarks>
/// Leurs identifiants sont <b>déterministes</b> et non aléatoires : ils doivent être identiques sur
/// toutes les installations, sinon un instantané de migration produit sur une machine renommerait
/// les colonnes système sur une autre.
/// </remarks>
public static class SystemFields
{
    /// <summary>Nom du champ d'identifiant.</summary>
    public const string Id = "id";

    /// <summary>Nom du champ de date de création.</summary>
    public const string Created = "created";

    /// <summary>Nom du champ de date de modification.</summary>
    public const string Updated = "updated";

    /// <summary>Nom du champ d'adresse de courriel des collections d'auth.</summary>
    public const string Email = "email";

    /// <summary>Nom du champ de visibilité de l'adresse.</summary>
    public const string EmailVisibility = "emailVisibility";

    /// <summary>Nom du champ d'adresse vérifiée.</summary>
    public const string Verified = "verified";

    /// <summary>Nom du champ de mot de passe. Jamais renvoyé par l'API.</summary>
    public const string Password = "password";

    /// <summary>Nom du champ de clé de jeton. Jamais renvoyé par l'API.</summary>
    public const string TokenKey = "tokenKey";

    /// <summary>Nom du champ portant les rôles d'un compte.</summary>
    public const string Roles = "roles";

    /// <summary>Nom du champ portant les permissions accordées directement à un compte.</summary>
    public const string Permissions = "permissions";

    /// <summary>
    /// Champ de confirmation du mot de passe : accepté en entrée, jamais stocké.
    /// </summary>
    /// <remarks>
    /// Il n'est pas une colonne, donc la validation le refuserait comme champ inconnu. Il figure ici
    /// pour être explicitement toléré — la tolérance est déclarée, pas laissée à un cas particulier
    /// enfoui dans le validateur.
    /// </remarks>
    public const string PasswordConfirm = "passwordConfirm";

    /// <summary>
    /// Champs acceptés dans un corps de requête sans correspondre à une colonne.
    /// </summary>
    public static IReadOnlySet<string> InputOnlyFields { get; } =
        new HashSet<string>(StringComparer.Ordinal) { PasswordConfirm };

    /// <summary>Champs présents sur toute collection.</summary>
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

    /// <summary>Champs supplémentaires d'une collection d'authentification.</summary>
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

        // Rôles et permissions sont des champs SYSTÈME, donc refusés dans un corps de requête
        // ordinaire. Les rendre modifiables par l'API des enregistrements permettrait à un compte
        // de s'accorder ses propres droits en POSTant « permissions: ['*'] » — dès lors que la
        // règle de création est ouverte, ce qui est le cas le plus courant sur une inscription.
        // L'attribution passe donc par un endpoint réservé au superadministrateur.
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

    /// <summary>Index imposés à une collection d'authentification.</summary>
    public static IReadOnlyList<CollectionIndex> AuthIndexes(string collectionName) =>
    [
        new CollectionIndex($"idx_{collectionName}_email", [Email], Unique: true),
        new CollectionIndex($"idx_{collectionName}_tokenKey", [TokenKey], Unique: true),
    ];

    /// <summary>Le champ est-il un champ système ?</summary>
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
