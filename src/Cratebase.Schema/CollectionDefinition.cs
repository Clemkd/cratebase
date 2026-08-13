using Cratebase.Core;

namespace Cratebase.Schema;

/// <summary>Nature d'une collection.</summary>
public enum CollectionKind
{
    /// <summary>Collection de données ordinaire.</summary>
    Base = 1,

    /// <summary>
    /// Collection d'authentification : porte des comptes, et expose les endpoints de connexion.
    /// Plusieurs peuvent coexister — utilisateurs et administrateurs de clients, par exemple.
    /// </summary>
    Auth = 2,

    /// <summary>
    /// Collection en lecture seule, alimentée par une requête.
    /// </summary>
    /// <remarks>
    /// ⚠️ Contrairement à PocketBase, la requête n'est <b>pas</b> du SQL brut : c'est une requête
    /// exprimée dans le langage de Cratebase. Autoriser du SQL rendrait la collection
    /// intransportable d'un moteur à l'autre, ce qui contredirait la raison d'être du projet.
    /// </remarks>
    View = 3,
}

/// <summary>
/// Règles d'accès d'une collection.
/// </summary>
/// <remarks>
/// <para>
/// Trois états par règle, repris de PocketBase et structurants :
/// </para>
/// <list type="bullet">
/// <item><see langword="null"/> — <b>verrouillée</b> : superadmin uniquement. C'est le défaut.</item>
/// <item>chaîne vide — ouverte à tous, visiteurs anonymes compris.</item>
/// <item>expression — autorisée si l'expression est vraie.</item>
/// </list>
/// <para>
/// Confondre <see langword="null"/> et la chaîne vide ouvrirait toutes les collections du système.
/// C'est pour cette raison que le type est <c>string?</c> et non <c>string</c> avec une convention.
/// </para>
/// </remarks>
public sealed record AccessRules
{
    /// <summary>Toutes les règles verrouillées : l'état d'une collection nouvellement créée.</summary>
    public static readonly AccessRules Locked = new();

    /// <summary>Règle de liste. Une violation rend une page vide, jamais une erreur.</summary>
    public string? List { get; init; }

    /// <summary>Règle de consultation. Une violation rend 404, pour ne pas divulguer l'existence.</summary>
    public string? View { get; init; }

    /// <summary>Règle de création. Une violation rend 400.</summary>
    public string? Create { get; init; }

    /// <summary>Règle de modification. Une violation rend 404.</summary>
    public string? Update { get; init; }

    /// <summary>Règle de suppression. Une violation rend 404.</summary>
    public string? Delete { get; init; }

    /// <summary>
    /// Règle de gestion, propre aux collections d'auth : qui peut modifier le mot de passe ou
    /// l'adresse d'un autre compte. Une violation rend 403.
    /// </summary>
    public string? Manage { get; init; }

    /// <summary>Renvoie la règle correspondant à une action.</summary>
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

/// <summary>Action soumise à une règle d'accès.</summary>
public enum CollectionAction
{
    /// <summary>Lister.</summary>
    List,

    /// <summary>Consulter une ligne.</summary>
    View,

    /// <summary>Créer.</summary>
    Create,

    /// <summary>Modifier.</summary>
    Update,

    /// <summary>Supprimer.</summary>
    Delete,

    /// <summary>Gérer un compte tiers.</summary>
    Manage,
}

/// <summary>
/// Index déclaré sur une collection.
/// </summary>
/// <param name="Name">Nom de l'index, unique dans la base.</param>
/// <param name="Fields">Champs indexés, dans l'ordre.</param>
/// <param name="Unique">L'index impose-t-il l'unicité ?</param>
public sealed record CollectionIndex(string Name, IReadOnlyList<string> Fields, bool Unique = false);

/// <summary>
/// Définition d'une collection.
/// </summary>
public sealed record CollectionDefinition
{
    /// <summary>Identifiant.</summary>
    public required RecordId Id { get; init; }

    /// <summary>Nom. Sert aussi de nom de table, donc validé par <see cref="Identifier"/>.</summary>
    public required string Name { get; init; }

    /// <summary>Nature.</summary>
    public CollectionKind Kind { get; init; } = CollectionKind.Base;

    /// <summary>Champs, champs système compris.</summary>
    public IReadOnlyList<FieldDefinition> Fields { get; init; } = [];

    /// <summary>Index déclarés.</summary>
    public IReadOnlyList<CollectionIndex> Indexes { get; init; } = [];

    /// <summary>Règles d'accès. Verrouillées par défaut.</summary>
    public AccessRules Rules { get; init; } = AccessRules.Locked;

    /// <summary>La collection appartient-elle au moteur ? Une collection système est protégée.</summary>
    public bool IsSystem { get; init; }

    /// <summary>Date de création.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>Date de dernière modification.</summary>
    public DateTimeOffset Updated { get; init; }

    /// <summary>Nom de la table portant les données.</summary>
    public string TableName => Name;

    /// <summary>Retrouve un champ par son nom.</summary>
    public FieldDefinition? Field(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
}
