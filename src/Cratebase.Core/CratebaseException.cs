namespace Cratebase.Core;

/// <summary>
/// Root of Cratebase's business errors. Each subtype carries the HTTP status it produces, so
/// translating to <c>ProblemDetails</c> never has to guess.
/// </summary>
public abstract class CratebaseException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Corresponding HTTP status.</summary>
    public abstract int StatusCode { get; }
}

/// <summary>
/// Invalid request: malformed body, unparsable filter, unknown field. Produces a 400.
/// </summary>
public sealed class CratebaseBadRequestException(string message, Exception? inner = null)
    : CratebaseException(message, inner)
{
    /// <inheritdoc />
    public override int StatusCode => 400;
}

/// <summary>
/// Validation failure on one or more fields. Produces a 400 with a per-field detail.
/// </summary>
public sealed class CratebaseValidationException(IReadOnlyDictionary<string, string[]> errors)
    : CratebaseException("Validation failed.")
{
    /// <summary>Error messages, indexed by field name.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    /// <inheritdoc />
    public override int StatusCode => 400;

    /// <summary>Builds a validation error for a single field.</summary>
    public static CratebaseValidationException ForField(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>
/// Unauthenticated caller where the action requires one. Produces a 401.
/// </summary>
public sealed class CratebaseUnauthenticatedException(string message = "Authentication required.")
    : CratebaseException(message)
{
    /// <inheritdoc />
    public override int StatusCode => 401;
}

/// <summary>
/// Authenticated caller lacking the required right. Produces a 403.
/// </summary>
/// <remarks>
/// Reserved for denials we accept disclosing: a locked rule (superuser only) or a missing RBAC
/// permission on the endpoint. A denial about a row's <b>existence</b> goes through
/// <see cref="CratebaseNotFoundException"/> instead — otherwise the 403 confirms the row exists.
/// </remarks>
public sealed class CratebaseForbiddenException(string message = "Insufficient permission.")
    : CratebaseException(message)
{
    /// <inheritdoc />
    public override int StatusCode => 403;
}

/// <summary>
/// Resource that does not exist, or is outside the authorized scope. Produces a 404.
/// </summary>
/// <remarks>
/// Violations of <c>viewRule</c>, <c>updateRule</c>, and <c>deleteRule</c> deliberately land here:
/// returning 403 would disclose that the row exists. This is PocketBase's semantics, kept for
/// exactly that reason.
/// </remarks>
public sealed class CratebaseNotFoundException(string message = "Resource not found.")
    : CratebaseException(message)
{
    /// <inheritdoc />
    public override int StatusCode => 404;
}

/// <summary>
/// Conflict: a uniqueness constraint was violated, or a concurrent write was lost. Produces a 409.
/// </summary>
public sealed class CratebaseConflictException(string message, Exception? inner = null)
    : CratebaseException(message, inner)
{
    /// <inheritdoc />
    public override int StatusCode => 409;
}
