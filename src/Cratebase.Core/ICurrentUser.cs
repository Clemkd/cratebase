namespace Cratebase.Core;

/// <summary>
/// Appelant de la requête courante.
/// </summary>
/// <remarks>
/// Porte l'enregistrement d'authentification complet, et pas seulement des claims : le langage de
/// filtre expose <c>@request.auth.*</c>, qui doit pouvoir atteindre n'importe quel champ de la
/// collection d'auth — y compris un champ ajouté après coup par l'utilisateur de la librairie.
/// </remarks>
public interface ICurrentUser
{
    /// <summary>Indique si un enregistrement d'authentification a été résolu.</summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Indique si l'appelant est superadmin. Un superadmin contourne les règles d'accès, jamais la
    /// validation ni les hooks.
    /// </summary>
    bool IsSuperuser { get; }

    /// <summary>Identifiant de l'enregistrement d'authentification, si authentifié.</summary>
    RecordId? Id { get; }

    /// <summary>Nom de la collection d'auth ayant authentifié l'appelant, si authentifié.</summary>
    string? CollectionName { get; }

    /// <summary>
    /// Permissions RBAC effectives, rôles et dérogations individuelles déjà fusionnés.
    /// </summary>
    IReadOnlyCollection<string> Permissions { get; }

    /// <summary>
    /// Champs de l'enregistrement d'authentification, pour la résolution de <c>@request.auth.*</c>.
    /// Le mot de passe et la clé de jeton en sont toujours absents.
    /// </summary>
    IReadOnlyDictionary<string, object?> Fields { get; }
}

/// <summary>Appelant anonyme.</summary>
public sealed class AnonymousUser : ICurrentUser
{
    /// <summary>Instance partagée.</summary>
    public static readonly AnonymousUser Instance = new();

    /// <inheritdoc />
    public bool IsAuthenticated => false;

    /// <inheritdoc />
    public bool IsSuperuser => false;

    /// <inheritdoc />
    public RecordId? Id => null;

    /// <inheritdoc />
    public string? CollectionName => null;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Permissions => [];

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Fields =>
        System.Collections.Frozen.FrozenDictionary<string, object?>.Empty;
}
