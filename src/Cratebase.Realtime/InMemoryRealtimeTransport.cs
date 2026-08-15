using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Cratebase.Realtime;

/// <summary>
/// Transport en mémoire de processus. Implémentation de départ, dans le conteneur unique.
/// </summary>
/// <remarks>
/// <para>
/// Un seul canal borné, lu par le concentrateur. Borné et non illimité : une rafale d'écritures
/// plus rapide que la diffusion ferait grossir une file sans limite jusqu'à la fin du processus, ce
/// qui échange une lenteur visible contre une panne mémoire qui ne l'est pas.
/// </para>
/// <para>
/// À saturation, ce sont les évènements <b>les plus anciens</b> qui tombent, et le compteur le dit.
/// L'inverse — refuser les nouveaux — figerait l'affichage des abonnés sur un état périmé tout en
/// laissant croire qu'il est à jour.
/// </para>
/// </remarks>
public sealed class InMemoryRealtimeTransport : IRealtimeTransport
{
    /// <summary>Nombre d'évènements en attente au-delà duquel les plus anciens sont écartés.</summary>
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

    /// <summary>Évènements perdus par saturation depuis le démarrage.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    public ValueTask PublishAsync(
        RealtimeEvent notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // `TryWrite` sur un canal `DropOldest` réussit toujours : le refus n'existe pas, mais
        // l'éviction, si. On la compte en comparant ce qui entre à ce qui sort, faute d'un signal
        // du canal lui-même.
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
