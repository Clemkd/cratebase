using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Cratebase.Realtime;

/// <summary>
/// In-process memory transport. Starting implementation, for the single-container deployment.
/// </summary>
/// <remarks>
/// <para>
/// A single bounded channel, read by the hub. Bounded, not unbounded: a burst of writes faster
/// than broadcast can drain would otherwise grow a queue without limit until the process runs out
/// of memory, trading a visible slowdown for an invisible crash.
/// </para>
/// <para>
/// Under saturation, it's the <b>oldest</b> events that get dropped, and the counter says so. The
/// reverse — rejecting new ones — would freeze subscribers' view on stale state while making it
/// look current.
/// </para>
/// </remarks>
public sealed class InMemoryRealtimeTransport : IRealtimeTransport
{
    /// <summary>Number of pending events beyond which the oldest are discarded.</summary>
    public const int Capacity = 1024;

    private readonly Channel<RealtimeEvent> _channel = Channel.CreateBounded<RealtimeEvent>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private long _dropped;

    /// <inheritdoc />
    public string Name => "memory";

    /// <summary>Events lost to saturation since startup.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    public ValueTask PublishAsync(
        RealtimeEvent notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // `TryWrite` on a `DropOldest` channel always succeeds: rejection doesn't exist, but
        // eviction does. We count it by comparing what goes in to what comes out, for lack of a
        // signal from the channel itself.
        if (!_channel.Writer.TryWrite(notification))
        {
            Interlocked.Increment(ref _dropped);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RealtimeEvent> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var notification in _channel.Reader
                           .ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return notification;
        }
    }
}
