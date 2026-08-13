using Cratebase.Core;
using Cratebase.Records;
using Cratebase.Schema;

namespace Cratebase.Auth;

/// <summary>
/// Traitement des champs d'authentification à l'écriture d'un enregistrement.
/// </summary>
/// <remarks>
/// C'est ce qui rend l'inscription possible : <c>password</c> est un champ système, donc écarté par
/// la validation pour empêcher qu'on écrive un condensat arbitraire. Le crochet le récupère dans le
/// corps brut, le vérifie, et pose le condensat lui-même.
/// </remarks>
public sealed class AuthRecordHook : IRecordMutationHook
{
    /// <summary>Nom du champ de confirmation du mot de passe.</summary>
    public const string PasswordConfirmField = "passwordConfirm";

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

        // Clé de jeton : sert de graine de révocation par compte. Elle est régénérée à chaque
        // changement de mot de passe, ce qui donne un second levier d'invalidation en plus de la
        // suppression des jetons.
        data[SystemFields.TokenKey] = RecordId.New().ToString();

        // Ni le rôle ni les permissions ne peuvent venir du client. Une inscription ouverte, avec
        // « permissions » modifiable, permettrait à n'importe qui de s'accorder tous les droits en
        // une requête. L'attribution passe par un endpoint réservé au superadministrateur.
        data[SystemFields.Roles] = Array.Empty<string>();
        data[SystemFields.Permissions] = Array.Empty<string>();
        data[SystemFields.Verified] = false;

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask BeforeUpdateAsync(
        CollectionDefinition collection,
        RecordData data,
        IReadOnlyDictionary<string, object?> submitted,
        IReadOnlyDictionary<string, object?> original,
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
            return ValueTask.CompletedTask;
        }

        ValidatePassword(password, submitted);

        data[SystemFields.Password] = PasswordHasher.Hash(password);
        data[SystemFields.TokenKey] = RecordId.New().ToString();

        return ValueTask.CompletedTask;
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
