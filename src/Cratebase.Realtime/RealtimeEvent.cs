using Cratebase.Records;

namespace Cratebase.Realtime;

/// <summary>
/// A write, as it travels to subscribers.
/// </summary>
/// <remarks>
/// The record is carried <b>whole</b> rather than reduced to its identifier. An event that only
/// said "row 42 changed" would force every subscriber to re-read it: a hundred clients subscribed
/// to an active collection would produce a hundred reads per write, and realtime would cost more
/// than the periodic refresh it replaces.
/// </remarks>
public sealed record RealtimeEvent
{
    /// <summary>Collection concerned.</summary>
    public required string Collection { get; init; }

    /// <summary>Record identifier.</summary>
    public required string RecordId { get; init; }

    /// <summary>Nature of the write.</summary>
    public required RecordAction Action { get; init; }

    /// <summary>
    /// Record after the write, or <see langword="null"/> after a deletion.
    /// </summary>
    /// <remarks>
    /// After a deletion, there's nothing left to filter through the view rule: the row no longer
    /// exists, so nobody can prove they had the right to see it. That's why a deletion is only
    /// announced to subscribers who could read the collection, and with no content beyond the
    /// identifier.
    /// </remarks>
    public IReadOnlyDictionary<string, object?>? Record { get; init; }
}

/// <summary>
/// Transport of events between the processes that write and the ones that broadcast.
/// </summary>
/// <remarks>
/// <para>
/// Abstraction declared from the first implementation, not bolted on afterward: it's the third
/// growth axis from §1.1 of the design document. In a single process, an in-memory bus suffices.
/// Across several instances behind a load balancer, the write lands on one and subscribers sit on
/// the others — a transport that crosses processes is then needed, PostgreSQL's
/// <c>LISTEN</c>/<c>NOTIFY</c>.
/// </para>
/// <para>
/// The contract is deliberately thin: publish, and read what has been published. Everything about
/// subscriptions and permissions lives above it, in the hub — otherwise every transport would have
/// to reimplement the access rules.
/// </para>
/// </remarks>
public interface IRealtimeTransport
{
    /// <summary>Transport name, for diagnostics.</summary>
    string Name { get; }

    /// <summary>Publishes an event.</summary>
    ValueTask PublishAsync(RealtimeEvent notification, CancellationToken cancellationToken = default);

    /// <summary>Reads the stream of published events, until cancelled.</summary>
    IAsyncEnumerable<RealtimeEvent> ReadAsync(CancellationToken cancellationToken = default);
}
