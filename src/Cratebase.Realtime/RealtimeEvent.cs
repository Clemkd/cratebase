using Cratebase.Records;

namespace Cratebase.Realtime;

/// <summary>
/// Une écriture, telle qu'elle circule vers les abonnés.
/// </summary>
/// <remarks>
/// L'enregistrement est porté <b>entier</b> plutôt que réduit à son identifiant. Un évènement qui
/// ne dirait que « la ligne 42 a changé » obligerait chaque abonné à la relire : cent clients
/// abonnés à une collection active produiraient cent lectures par écriture, et le temps réel
/// coûterait plus cher que le rafraîchissement périodique qu'il remplace.
/// </remarks>
public sealed record RealtimeEvent
{
    /// <summary>Collection concernée.</summary>
    public required string Collection { get; init; }

    /// <summary>Identifiant de l'enregistrement.</summary>
    public required string RecordId { get; init; }

    /// <summary>Nature de l'écriture.</summary>
    public required RecordAction Action { get; init; }

    /// <summary>
    /// Enregistrement après écriture, ou <see langword="null"/> après une suppression.
    /// </summary>
    /// <remarks>
    /// Après suppression, il n'y a plus rien à filtrer par la règle de consultation : la ligne
    /// n'existe plus, donc personne ne peut prouver qu'il avait le droit de la voir. C'est pourquoi
    /// une suppression n'est annoncée qu'aux abonnés qui pouvaient lire la collection, et sans autre
    /// contenu que l'identifiant.
    /// </remarks>
    public IReadOnlyDictionary<string, object?>? Record { get; init; }
}

/// <summary>
/// Transport des évènements entre les processus qui écrivent et ceux qui diffusent.
/// </summary>
/// <remarks>
/// <para>
/// Abstraction déclarée dès la première implémentation, et non ajoutée après coup : c'est le
/// troisième axe de croissance du §1.1 du document de conception. En un seul processus, un bus
/// mémoire suffit. À plusieurs instances derrière un répartiteur, l'écriture arrive sur l'une et
/// les abonnés sont sur les autres — il faut alors un transport qui traverse les processus,
/// <c>LISTEN</c>/<c>NOTIFY</c> côté PostgreSQL.
/// </para>
/// <para>
/// Le contrat est volontairement pauvre : publier, et lire ce qui a été publié. Tout ce qui relève
/// des abonnements et des droits vit au-dessus, dans le concentrateur — sans quoi chaque transport
/// devrait réimplémenter les règles d'accès.
/// </para>
/// </remarks>
public interface IRealtimeTransport
{
    /// <summary>Nom du transport, pour les diagnostics.</summary>
    string Name { get; }

    /// <summary>Publie un évènement.</summary>
    ValueTask PublishAsync(RealtimeEvent notification, CancellationToken cancellationToken = default);

    /// <summary>Lit le flux des évènements publiés, jusqu'à annulation.</summary>
    IAsyncEnumerable<RealtimeEvent> ReadAsync(CancellationToken cancellationToken = default);
}
