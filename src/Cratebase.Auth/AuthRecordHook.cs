using Cratebase.Core;
using Cratebase.Records;
using Cratebase.Schema;

namespace Cratebase.Auth;

/// <summary>
/// Processing of authentication fields when a record is written.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes sign-up possible: <c>password</c> is a system field, so it's stripped by
/// validation to prevent an arbitrary digest from being written. The hook retrieves it from the
/// raw body, verifies it, and sets the digest itself.
/// </para>
/// <para>
/// It also carries the two invariants the engine cannot hand off to an access rule: a password
/// change <b>revokes sessions</b>, and the last superuser cannot be deleted.
/// </para>
/// </remarks>
public sealed class AuthRecordHook(AuthService auth, AuthTokenStore tokens) : IRecordMutationHook
{
    /// <summary>Name of the password confirmation field.</summary>
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
                SystemFields.Password, "A password is required to create an account.");
        }

        ValidatePassword(password, submitted);

        data[SystemFields.Password] = PasswordHasher.Hash(password);

        // Token key: a generation marker for the account, regenerated on every password change.
        // Effective revocation itself goes through deleting the tokens — see BeforeUpdateAsync.
        data[SystemFields.TokenKey] = RecordId.New().ToString();

        // Neither the role nor the permissions can come from the client. An open sign-up with a
        // modifiable "permissions" field would let anyone grant themselves every right in one
        // request. Assignment goes through a superuser-only endpoint.
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

        // ⚠️ Sessions are actually destroyed, not merely marked: rotating "tokenKey" is checked
        // nowhere when resolving a token. Without this line, changing a password after a session
        // theft wouldn't sign the thief out — yet that's the user's first instinct, and they'd
        // believe the problem solved.
        //
        // Revocation happens before the write: if the write then fails, sessions were closed for
        // nothing — that's the safe direction of the error, the reverse would leave sessions open
        // on a password that has changed.
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

        // ⚠️ Deleting the last superuser makes the instance unadministrable: every system
        // collection is locked, and bootstrap only recreates an account if configuration carries
        // one — which an ordinary deployment doesn't. No access rule can express this guard, since
        // it concerns precisely the account that has the right to do everything.
        var only = await _auth.OnlySuperuserIdAsync(cancellationToken).ConfigureAwait(false);

        if (only is not null && string.Equals(only, recordId, StringComparison.Ordinal))
        {
            throw new CratebaseConflictException(
                "This account is the last superuser: deleting it would make the instance " +
                "unadministrable. Create another one before deleting this one.");
        }
    }

    private static void ValidatePassword(string password, IReadOnlyDictionary<string, object?> submitted)
    {
        if (password.Length < PasswordHasher.MinimumLength)
        {
            throw CratebaseValidationException.ForField(
                SystemFields.Password,
                $"The password must be at least {PasswordHasher.MinimumLength} characters long.");
        }

        // Confirmation is only required if it's submitted: a programmatic client has no reason to
        // supply it, a form does.
        var confirmation = submitted.GetValueOrDefault(PasswordConfirmField);

        if (confirmation is not null && !string.Equals(AsText(confirmation), password, StringComparison.Ordinal))
        {
            throw CratebaseValidationException.ForField(
                PasswordConfirmField, "The confirmation does not match the password.");
        }
    }

    private static string AsText(object? value) => value as string ?? string.Empty;
}
