using Cratebase.Core;
using Cratebase.Records;
using Cratebase.Schema;

namespace Cratebase.Auth;

/// <summary>
/// Traitement des champs d'authentification à l'écriture d'un enregistrement.
/// </summary>
/// <remarks>
/// <para>
/// C'est ce qui rend l'inscription possible : <c>password</c> est un champ système, donc écarté par
/// la validation pour empêcher qu'on écrive un condensat arbitraire. Le crochet le récupère dans le
/// corps brut, le vérifie, et pose le condensat lui-même.
/// </para>
/// <para>
/// Il porte aussi les deux invariantes que le moteur ne peut pas confier à une règle d'accès : un
/// changement de mot de passe <b>révoque les sessions</b>, et le dernier super-admin ne
/// peut pas être supprimé.
/// </para>
/// </remarks>
public sealed class AuthRecordHook(AuthService auth, AuthTokenStore tokens) : IRecordMutationHook
{
    /// <summary>Nom du champ de confirmation du mot de passe.</summary>
    public const string PasswordConfirmField = "password_confirm";

    private readonly AuthService _auth = auth ?? throw new ArgumentNullException(nameof(auth));

    private readonly AuthTokenStore _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));

    /// <inheritdoc />
    public ValueTask BeforeCreateAsync(
        CollectionDefinition collection,
        RecordData data,
        IReadOnlyDictionary<string, object?> submitted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(submitted);

        if (collection.Kind is not CollectionKind.Auth)
        {
            return ValueTask.CompletedTask;
        }

        var password = AsText(submitted.GetValueOrDefault(SystemFields.Password));

        if (password.Length == 0)
        {
            throw CratebaseValidationException.ForField(
                SystemFields.Password, "Un mot de passe est obligatoire pour créer un compte.");
        }

        ValidatePassword(password, submitted);

        data[SystemFields.Password] = PasswordHasher.Hash(password);

        // Clé de jeton : marque de génération du compte, régénérée à chaque changement de mot de
        // passe. La révocation effective, elle, passe par la suppression des jetons — voir
        // BeforeUpdateAsync.
        data[SystemFields.TokenKey] = RecordId.New().ToString();

        // Ni le rôle ni les permissions ne peuvent venir du client. Une inscription ouverte, avec
        // « permissions » modifiable, permettrait à n'importe qui de s'accorder tous les droits en
        // une requête. L'attribution passe par un endpoint réservé au super-admin.
        data[SystemFields.Roles] = Array.Empty<string>();
        data[SystemFields.Permissions] = Array.Empty<string>();
        data[SystemFields.Verified] = false;

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask BeforeUpdateAsync(
        CollectionDefinition collection,
        RecordData data,
        IReadOnlyDictionary<string, object?> submitted,
        IReadOnlyDictionary<string, object?> original,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(original);

        if (collection.Kind is not CollectionKind.Auth)
        {
            return;
        }

        var password = AsText(submitted.GetValueOrDefault(SystemFields.Password));

        if (password.Length == 0)
        {
            return;
        }

        ValidatePassword(password, submitted);

        data[SystemFields.Password] = PasswordHasher.Hash(password);
        data[SystemFields.TokenKey] = RecordId.New().ToString();

        // ⚠️ Les sessions sont réellement détruites, et pas seulement marquées : la rotation de
        // « tokenKey » n'est consultée nulle part à la résolution d'un jeton. Sans cette ligne,
        // changer son mot de passe après un vol de session ne déconnecte pas le voleur — c'est
        // pourtant le premier réflexe de l'utilisateur, et il croirait le problème réglé.
        //
        // La révocation précède l'écriture : si celle-ci échoue, on aura déconnecté pour rien —
        // c'est le sens sûr de l'erreur, l'inverse laisserait des sessions ouvertes sur un mot de
        // passe changé.
        if (RecordId.TryParse(AsText(original.GetValueOrDefault(SystemFields.Id)), out var id))
        {
            await _tokens.RevokeAllAsync(collection.Name, id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask BeforeDeleteAsync(
        CollectionDefinition collection,
        string recordId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);

        if (!string.Equals(collection.Name, AuthService.SuperusersCollection, StringComparison.Ordinal))
        {
            return;
        }

        // ⚠️ Supprimer le dernier super-admin rend l'instance inadministrable : toutes les
        // collections système sont verrouillées, et l'amorçage ne recrée un compte que si la
        // configuration en porte un — ce qui n'est pas le cas d'un déploiement ordinaire. Aucune
        // règle d'accès ne peut exprimer cette garde, puisqu'elle porte justement sur le compte qui
        // a le droit de tout faire.
        var only = await _auth.OnlySuperuserIdAsync(cancellationToken).ConfigureAwait(false);

        if (only is not null && string.Equals(only, recordId, StringComparison.Ordinal))
        {
            throw new CratebaseConflictException(
                "Ce compte est le dernier super-admin : le supprimer rendrait l'instance " +
                "inadministrable. Créez-en un autre avant de supprimer celui-ci.");
        }
    }

    private static void ValidatePassword(string password, IReadOnlyDictionary<string, object?> submitted)
    {
        if (password.Length < PasswordHasher.MinimumLength)
        {
            throw CratebaseValidationException.ForField(
                SystemFields.Password,
                $"Le mot de passe doit compter au moins {PasswordHasher.MinimumLength} caractères.");
        }

        // La confirmation n'est exigée que si elle est soumise : un client programmatique n'a
        // aucune raison de la fournir, un formulaire si.
        var confirmation = submitted.GetValueOrDefault(PasswordConfirmField);

        if (confirmation is not null && !string.Equals(AsText(confirmation), password, StringComparison.Ordinal))
        {
            throw CratebaseValidationException.ForField(
                PasswordConfirmField, "La confirmation ne correspond pas au mot de passe.");
        }
    }

    private static string AsText(object? value) => value as string ?? string.Empty;
}
