using Cratebase.Records;
using Cratebase.Schema;

namespace Cratebase.Realtime;

/// <summary>
/// Publishes every validated write to the realtime transport.
/// </summary>
/// <remarks>
/// This is an ordinary after-write hook, not a privileged tap into the record engine: realtime
/// needs nothing that the library doesn't already offer to anyone who wants to react to writes. A
/// user who wants to send an email, feed a search index, or push to another bus writes exactly the
/// same kind of class and registers it the same way.
/// </remarks>
public sealed class RealtimeRecordHook(IRealtimeTransport transport) : IRecordMutationHook
{
    private readonly IRealtimeTransport _transport = transport
        ?? throw new ArgumentNullException(nameof(transport));

    /// <inheritdoc />
    public async ValueTask AfterWriteAsync(
        CollectionDefinition collection,
        RecordAction action,
        string recordId,
        IReadOnlyDictionary<string, object?>? record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);

        await _transport.PublishAsync(
                new RealtimeEvent
                {
                    Collection = collection.Name,
                    RecordId = recordId,
                    Action = action,
                    Record = record,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
