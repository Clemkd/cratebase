using Cratebase.Records;
using Cratebase.Schema;

namespace Cratebase.Realtime;

/// <summary>
/// Publie chaque écriture validée sur le transport temps réel.
/// </summary>
/// <remarks>
/// C'est un crochet d'après-écriture ordinaire, et pas un branchement privilégié dans le moteur
/// d'enregistrements : le temps réel n'a besoin de rien que la librairie n'offre à qui veut réagir
/// aux écritures. Un utilisateur qui veut envoyer un courriel, alimenter un index de recherche ou
/// pousser vers un autre bus écrit exactement le même genre de classe et l'enregistre de la même
/// façon.
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
