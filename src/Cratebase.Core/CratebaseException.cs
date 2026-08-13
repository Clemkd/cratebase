namespace Cratebase.Core;

/// <summary>
/// Racine des erreurs métier de Cratebase. Chaque sous-type porte le statut HTTP qu'il produit,
/// pour que la traduction en <c>ProblemDetails</c> n'ait rien à deviner.
/// </summary>
public abstract class CratebaseException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Statut HTTP correspondant.</summary>
    public abstract int StatusCode { get; }
}

/// <summary>
/// Requête invalide : corps mal formé, filtre non analysable, champ inconnu. Produit un 400.
/// </summary>
public sealed class CratebaseBadRequestException(string message, Exception? inner = null)
    : CratebaseException(message, inner)
{
    /// <inheritdoc />
    public override int StatusCode => 400;
}

/// <summary>
/// Échec de validation d'un ou plusieurs champs. Produit un 400 avec le détail par champ.
/// </summary>
public sealed class CratebaseValidationException(IReadOnlyDictionary<string, string[]> errors)
    : CratebaseException("La validation a échoué.")
{
    /// <summary>Messages d'erreur, indexés par nom de champ.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    /// <inheritdoc />
    public override int StatusCode => 400;

    /// <summary>Construit une erreur de validation portant sur un seul champ.</summary>
    public static CratebaseValidationException ForField(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>
/// Appelant non authentifié alors que l'action l'exige. Produit un 401.
/// </summary>
public sealed class CratebaseUnauthenticatedException(string message = "Authentification requise.")
    : CratebaseException(message)
{
    /// <inheritdoc />
    public override int StatusCode => 401;
}

/// <summary>
/// Appelant authentifié mais sans le droit demandé. Produit un 403.
/// </summary>
/// <remarks>
/// Réservé aux refus qu'on assume de divulguer : règle verrouillée (superadmin seulement) ou
/// permission RBAC manquante sur l'endpoint. Un refus portant sur <b>l'existence</b> d'une ligne
/// passe par <see cref="CratebaseNotFoundException"/>, sans quoi le 403 confirme que la ligne
/// existe.
/// </remarks>
public sealed class CratebaseForbiddenException(string message = "Droit insuffisant.")
    : CratebaseException(message)
{
    /// <inheritdoc />
    public override int StatusCode => 403;
}

/// <summary>
/// Ressource inexistante, ou hors du périmètre autorisé. Produit un 404.
/// </summary>
/// <remarks>
/// Les violations de <c>viewRule</c>, <c>updateRule</c> et <c>deleteRule</c> passent ici
/// volontairement : renvoyer 403 divulguerait l'existence de la ligne. C'est la sémantique de
/// PocketBase, reprise pour cette raison précise.
/// </remarks>
public sealed class CratebaseNotFoundException(string message = "Ressource introuvable.")
    : CratebaseException(message)
{
    /// <inheritdoc />
    public override int StatusCode => 404;
}

/// <summary>
/// Conflit : contrainte d'unicité violée, ou écriture concurrente perdue. Produit un 409.
/// </summary>
public sealed class CratebaseConflictException(string message, Exception? inner = null)
    : CratebaseException(message, inner)
{
    /// <inheritdoc />
    public override int StatusCode => 409;
}
