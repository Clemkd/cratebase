using Cratebase.Schema;

namespace Cratebase.Records;

/// <summary>
/// Intervention on a record before it is written.
/// </summary>
/// <remarks>
/// <para>
/// The first link of the public hook system (the equivalent of PocketBase's <c>OnRecordCreate</c>).
/// It exists first for a concrete need: on an authentication collection, the submitted password
/// must be hashed before writing — but <c>password</c> is a system field, so it's stripped by
/// validation. Without this extension point, no account could ever be created through the API.
/// </para>
/// <para>
/// The order is fixed: <b>validation, then hooks, then the create rule</b>. Hooks therefore see
/// already-validated data, and the access rule sees the record exactly as it will be written,
/// including whatever the hooks set.
/// </para>
/// </remarks>
public interface IRecordMutationHook
{
    /// <summary>
    /// Runs before a creation.
    /// </summary>
    /// <param name="collection">Target collection.</param>
    /// <param name="data">Validated values, mutable.</param>
    /// <param name="submitted">
    /// Raw body as submitted, <b>before</b> validation. The only place to find a system field
    /// stripped by validation, <c>password</c> in particular.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Empty default implementation, as with the other members: a hook written only for realtime
    /// shouldn't have to declare three methods it would leave empty. This is what makes the
    /// interface usable in library mode without ceremony.
    /// </remarks>
    ValueTask BeforeCreateAsync(
        CollectionDefinition collection,
        RecordData data,
        IReadOnlyDictionary<string, object?> submitted,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Runs before a modification.</summary>
    /// <param name="collection">Target collection.</param>
    /// <param name="data">Validated values, mutable.</param>
    /// <param name="submitted">Raw body as submitted.</param>
    /// <param name="original">Prior state of the record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask BeforeUpdateAsync(
        CollectionDefinition collection,
        RecordData data,
        IReadOnlyDictionary<string, object?> submitted,
        IReadOnlyDictionary<string, object?> original,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Runs before a deletion. Throwing here cancels the deletion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record is <b>not</b> loaded for the occasion: deletion is a single statement, access
    /// rule included, and reading the row first would add a round-trip to every deletion for the
    /// sole benefit of hooks that want its content. A hook that needs the values reads them itself.
    /// </para>
    /// <para>
    /// Empty default implementation: a hook written only for creations has nothing to add to keep
    /// compiling.
    /// </para>
    /// </remarks>
    /// <param name="collection">Target collection.</param>
    /// <param name="recordId">Targeted identifier, as submitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask BeforeDeleteAsync(
        CollectionDefinition collection,
        string recordId,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Runs <b>after</b> a write that succeeded and was validated by the access rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second half of the hook system, and the only one that can say "this happened". The
    /// before-write hooks can still fail, be cancelled by the create rule, or vanish with the
    /// transaction; this one is called once the transaction has committed. That's what makes it the
    /// anchor point for realtime, notification emails, and anything that must not fire for a write
    /// that didn't succeed.
    /// </para>
    /// <para>
    /// It can cancel nothing: throwing here doesn't undo the write, it only returns an error to a
    /// caller whose request nonetheless succeeded. Exceptions are therefore absorbed by the engine
    /// — an unreachable subscriber doesn't break a creation.
    /// </para>
    /// </remarks>
    /// <param name="collection">Target collection.</param>
    /// <param name="action">Nature of the write.</param>
    /// <param name="recordId">Record identifier.</param>
    /// <param name="record">
    /// Record as it now stands in the database, or <see langword="null"/> after a deletion.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask AfterWriteAsync(
        CollectionDefinition collection,
        RecordAction action,
        string recordId,
        IReadOnlyDictionary<string, object?>? record,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Nature of a write, as after-write hooks receive it.</summary>
public enum RecordAction
{
    /// <summary>Creation.</summary>
    Create,

    /// <summary>Modification.</summary>
    Update,

    /// <summary>Deletion.</summary>
    Delete,
}
