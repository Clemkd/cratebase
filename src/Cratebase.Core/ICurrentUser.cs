namespace Cratebase.Core;

/// <summary>
/// Caller of the current request.
/// </summary>
/// <remarks>
/// Carries the full auth record, not just claims: the filter language exposes
/// <c>@request.auth.*</c>, which must be able to reach any field of the auth collection —
/// including a field added later by the library's user.
/// </remarks>
public interface ICurrentUser
{
    /// <summary>Indicates whether an auth record was resolved.</summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Indicates whether the caller is a superuser. A superuser bypasses access rules, never
    /// validation or hooks.
    /// </summary>
    bool IsSuperuser { get; }

    /// <summary>Auth record identifier, if authenticated.</summary>
    RecordId? Id { get; }

    /// <summary>Name of the auth collection that authenticated the caller, if authenticated.</summary>
    string? CollectionName { get; }

    /// <summary>
    /// Effective RBAC permissions, roles and individual overrides already merged.
    /// </summary>
    IReadOnlyCollection<string> Permissions { get; }

    /// <summary>
    /// Fields of the auth record, for resolving <c>@request.auth.*</c>. The password and token key
    /// are always absent from it.
    /// </summary>
    IReadOnlyDictionary<string, object?> Fields { get; }
}

/// <summary>Anonymous caller.</summary>
public sealed class AnonymousUser : ICurrentUser
{
    /// <summary>Shared instance.</summary>
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
