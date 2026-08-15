using System.Collections.Concurrent;
using System.Threading.Channels;
using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Records;
using Cratebase.Schema;

namespace Cratebase.Realtime;

/// <summary>
/// Appelant d'un client temps réel, figé à la connexion.
/// </summary>
/// <remarks>
/// Une copie, et non l'appelant de la requête HTTP : celui-ci est résolu par requête et n'a aucune
/// raison de survivre à celle qui a ouvert le flux. Le figer a une conséquence qu'il faut nommer —
/// des droits retirés pendant la connexion ne le sont pas pour ce flux —, d'où la révocation
/// explicite du jeton, qui ferme les flux ouverts de ce compte.
/// </remarks>
public sealed record RealtimePrincipal(
    bool IsAuthenticated,
    bool IsSuperuser,
    RecordId? Id,
    string? CollectionName,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyDictionary<string, object?> Fields) : ICurrentUser
{
    /// <summary>Fige l'appelant courant.</summary>
    public static RealtimePrincipal From(ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new RealtimePrincipal(
            user.IsAuthenticated,
            user.IsSuperuser,
            user.Id,
            user.CollectionName,
            [.. user.Permissions],
            new Dictionary<string, object?>(user.Fields, StringComparer.Ordinal));
    }
}

/// <summary>
/// Un client connecté et ce à quoi il s'est abonné.
/// </summary>
public sealed class RealtimeClient
{
    /// <summary>Taille de la file d'attente d'un client, en messages.</summary>
    public const int Capacity = 64;

    private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
        new BoundedChannelOptions(Capacity)
        {
            // Un client lent perd ses messages les plus anciens plutôt que de ralentir la
            // diffusion : sans cela, un onglet en veille sur un réseau saturé bloquerait le flux de
            // tous les autres.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    /// <summary>Identifiant remis au client à la connexion.</summary>
    public string Id { get; } = RecordId.New().ToString();

    /// <summary>Appelant figé à la connexion.</summary>
    public RealtimePrincipal Principal { get; internal set; } = RealtimePrincipal.From(AnonymousUser.Instance);

    /// <summary>Sujets suivis : <c>collection</c> ou <c>collection/identifiant</c>.</summary>
    public IReadOnlySet<string> Topics { get; internal set; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Dépose un message. Sans effet si le client est déjà fermé.</summary>
    internal void Post(string payload) => _outbox.Writer.TryWrite(payload);

    /// <summary>Lit les messages destinés à ce client, jusqu'à fermeture.</summary>
    public IAsyncEnumerable<string> ReadAsync(CancellationToken cancellationToken = default) =>
        _outbox.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Ferme le flux du client.</summary>
    internal void Close() => _outbox.Writer.TryComplete();
}

/// <summary>
/// Concentrateur du temps réel : qui écoute quoi, et qui a le droit de le voir.
/// </summary>
/// <remarks>
/// <para>
/// <b>La règle de consultation est réévaluée à la diffusion.</b> C'est le point qui décide de la
/// valeur de tout le reste : sans lui, s'abonner à une collection suffirait à recevoir des
/// enregistrements que l'API refuse de servir, et le temps réel deviendrait un contournement de
/// toutes les règles d'accès à la fois.
/// </para>
/// <para>
/// Les deux cas fréquents ne coûtent aucune requête : une règle verrouillée ne concerne que les
/// super-admins, une règle ouverte concerne tout le monde. Seule une règle conditionnelle demande
/// une lecture — une par appelant distinct, pas une par abonné.
/// </para>
/// </remarks>
public sealed class RealtimeHub(CollectionRegistry registry, RecordService records)
{
    private readonly CollectionRegistry _registry = registry
        ?? throw new ArgumentNullException(nameof(registry));

    private readonly RecordService _records = records ?? throw new ArgumentNullException(nameof(records));

    private readonly ConcurrentDictionary<string, RealtimeClient> _clients = new(StringComparer.Ordinal);

    /// <summary>Clients connectés.</summary>
    public int Count => _clients.Count;

    /// <summary>Une collection est-elle suivie par au moins un client ?</summary>
    /// <remarks>
    /// Consultée avant de payer une lecture supplémentaire : le contenu d'un enregistrement
    /// supprimé n'est chargé que si quelqu'un attend de l'apprendre.
    /// </remarks>
    public bool IsWatched(string collection) =>
        _clients.Values.Any(client => client.Topics.Any(topic => Matches(topic, collection)));

    /// <summary>Enregistre un client et lui attribue son identifiant.</summary>
    public RealtimeClient Connect(ICurrentUser user)
    {
        var client = new RealtimeClient { Principal = RealtimePrincipal.From(user) };

        _clients[client.Id] = client;

        return client;
    }

    /// <summary>Retire un client et ferme son flux.</summary>
    public void Disconnect(string clientId)
    {
        if (_clients.TryRemove(clientId, out var client))
        {
            client.Close();
        }
    }

    /// <summary>
    /// Remplace la liste des sujets suivis par un client.
    /// </summary>
    /// <remarks>
    /// Remplace, et n'ajoute pas : un client qui change d'écran doit pouvoir se désabonner de tout
    /// en une requête. L'appelant est réactualisé au passage — c'est ainsi qu'une console qui se
    /// connecte avant d'être authentifiée devient un abonné authentifié sans rouvrir son flux.
    /// </remarks>
    public bool Subscribe(string clientId, IEnumerable<string> topics, ICurrentUser user)
    {
        if (!_clients.TryGetValue(clientId, out var client)) return false;

        client.Topics = new HashSet<string>(
            (topics ?? []).Where(topic => !string.IsNullOrWhiteSpace(topic)).Select(topic => topic.Trim()),
            StringComparer.Ordinal);

        client.Principal = RealtimePrincipal.From(user);

        return true;
    }

    /// <summary>Ferme tous les flux d'un compte. Appelé à la révocation de ses jetons.</summary>
    public void DisconnectAccount(string collection, RecordId id)
    {
        foreach (var (key, client) in _clients)
        {
            if (client.Principal.Id != id) continue;
            if (!string.Equals(client.Principal.CollectionName, collection, StringComparison.Ordinal)) continue;

            if (_clients.TryRemove(key, out var removed)) removed.Close();
        }
    }

    /// <summary>Diffuse un évènement aux abonnés qui y ont droit.</summary>
    public async Task DispatchAsync(
        RealtimeEvent notification,
        Func<RealtimeEvent, string, string> render,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(render);

        var collection = _registry.Find(notification.Collection);

        if (collection is null) return;

        // Le verdict est calculé une fois par appelant distinct, pas une fois par abonné : dix
        // onglets du même compte ne valent pas dix évaluations de la même règle.
        var verdicts = new Dictionary<RealtimePrincipal, bool>();

        foreach (var client in _clients.Values)
        {
            var topic = client.Topics.FirstOrDefault(entry =>
                Matches(entry, notification.Collection, notification.RecordId));

            if (topic is null) continue;

            if (!verdicts.TryGetValue(client.Principal, out var allowed))
            {
                allowed = await CanSeeAsync(collection, notification, client.Principal, cancellationToken)
                    .ConfigureAwait(false);

                verdicts[client.Principal] = allowed;
            }

            if (allowed) client.Post(render(notification, topic));
        }
    }

    /// <summary>Le sujet suivi désigne-t-il cette collection, éventuellement cet enregistrement ?</summary>
    private static bool Matches(string topic, string collection, string? recordId = null)
    {
        if (string.Equals(topic, collection, StringComparison.Ordinal)) return true;

        return recordId is not null
            && string.Equals(topic, $"{collection}/{recordId}", StringComparison.Ordinal);
    }

    /// <summary>
    /// L'appelant a-t-il le droit de voir cet enregistrement ?
    /// </summary>
    /// <remarks>
    /// La règle est celle de la consultation, appliquée par le moteur d'enregistrements lui-même :
    /// réimplémenter ici une évaluation « équivalente » garantirait qu'elle diverge un jour, et la
    /// divergence irait dans le sens de la fuite.
    /// </remarks>
    private async Task<bool> CanSeeAsync(
        CollectionDefinition collection,
        RealtimeEvent notification,
        RealtimePrincipal principal,
        CancellationToken cancellationToken)
    {
        if (principal.IsSuperuser) return true;

        var rule = collection.Rules.View;

        // Verrouillée : réservée aux super-admins, déjà rendus plus haut.
        if (rule is null) return false;

        // Ouverte à tous : rien à évaluer.
        if (rule.Length == 0) return true;

        // Une suppression ne laisse rien à évaluer : la ligne n'existe plus, donc la règle ne peut
        // plus être appliquée. On refuse plutôt que de supposer — un abonné qui ne voyait pas
        // l'enregistrement n'a pas à apprendre qu'il a disparu.
        if (notification.Action is RecordAction.Delete && notification.Record is null) return false;

        try
        {
            await _records.ViewAsync(
                    collection.Name,
                    notification.RecordId,
                    new FilterRequestContext { Auth = principal, Context = "realtime", Method = "GET" },
                    cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (CratebaseException)
        {
            return false;
        }
    }
}
