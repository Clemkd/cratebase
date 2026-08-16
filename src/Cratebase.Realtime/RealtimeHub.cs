using System.Collections.Concurrent;
using System.Threading.Channels;
using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Records;
using Cratebase.Schema;

namespace Cratebase.Realtime;

/// <summary>
/// Caller of a realtime client, frozen at connection time.
/// </summary>
/// <remarks>
/// A copy, not the HTTP request's caller: that one is resolved per request and has no reason to
/// outlive the request that opened the stream. Freezing it has a consequence worth naming —
/// permissions revoked during the connection don't apply to this stream — hence the explicit token
/// revocation, which closes the open streams of that account.
/// </remarks>
public sealed record RealtimePrincipal(
    bool IsAuthenticated,
    bool IsSuperuser,
    RecordId? Id,
    string? CollectionName,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyDictionary<string, object?> Fields) : ICurrentUser
{
    /// <summary>Freezes the current caller.</summary>
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
/// A connected client and what it has subscribed to.
/// </summary>
public sealed class RealtimeClient
{
    /// <summary>Size of a client's outbox, in messages.</summary>
    public const int Capacity = 64;

    private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
        new BoundedChannelOptions(Capacity)
        {
            // A slow client loses its oldest messages rather than slowing down the broadcast:
            // without this, one tab asleep on a saturated network would block the stream for
            // everyone else.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    /// <summary>Identifier handed to the client on connection.</summary>
    public string Id { get; } = RecordId.New().ToString();

    /// <summary>Caller frozen at connection time.</summary>
    public RealtimePrincipal Principal { get; internal set; } = RealtimePrincipal.From(AnonymousUser.Instance);

    /// <summary>Followed topics: <c>collection</c> or <c>collection/id</c>.</summary>
    public IReadOnlySet<string> Topics { get; internal set; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Enqueues a message. No effect if the client is already closed.</summary>
    internal void Post(string payload) => _outbox.Writer.TryWrite(payload);

    /// <summary>Reads the messages addressed to this client, until closed.</summary>
    public IAsyncEnumerable<string> ReadAsync(CancellationToken cancellationToken = default) =>
        _outbox.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Closes the client's stream.</summary>
    internal void Close() => _outbox.Writer.TryComplete();
}

/// <summary>
/// Realtime hub: who is listening to what, and who is allowed to see it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The view rule is re-evaluated at broadcast time.</b> This is the point that decides the
/// value of everything else: without it, subscribing to a collection would be enough to receive
/// records the API refuses to serve, and realtime would become a bypass of every access rule at
/// once.
/// </para>
/// <para>
/// The two common cases cost no query: a locked rule concerns only superusers, an open rule
/// concerns everyone. Only a conditional rule requires a read — one per distinct caller, not one
/// per subscriber.
/// </para>
/// </remarks>
public sealed class RealtimeHub(CollectionRegistry registry, RecordService records)
{
    private readonly CollectionRegistry _registry = registry
        ?? throw new ArgumentNullException(nameof(registry));

    private readonly RecordService _records = records ?? throw new ArgumentNullException(nameof(records));

    private readonly ConcurrentDictionary<string, RealtimeClient> _clients = new(StringComparer.Ordinal);

    /// <summary>Connected clients.</summary>
    public int Count => _clients.Count;

    /// <summary>Is a collection followed by at least one client?</summary>
    /// <remarks>
    /// Checked before paying for an extra read: the content of a deleted record is only loaded if
    /// someone is waiting to learn about it.
    /// </remarks>
    public bool IsWatched(string collection) =>
        _clients.Values.Any(client => client.Topics.Any(topic => Matches(topic, collection)));

    /// <summary>Registers a client and assigns its identifier.</summary>
    public RealtimeClient Connect(ICurrentUser user)
    {
        var client = new RealtimeClient { Principal = RealtimePrincipal.From(user) };

        _clients[client.Id] = client;

        return client;
    }

    /// <summary>Removes a client and closes its stream.</summary>
    public void Disconnect(string clientId)
    {
        if (_clients.TryRemove(clientId, out var client))
        {
            client.Close();
        }
    }

    /// <summary>
    /// Replaces the list of topics a client follows.
    /// </summary>
    /// <remarks>
    /// Replaces, doesn't add: a client switching screens must be able to unsubscribe from
    /// everything in one request. The caller is refreshed along the way — that's how a console that
    /// connects before authenticating becomes an authenticated subscriber without reopening its
    /// stream.
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

    /// <summary>Closes every stream of an account. Called when its tokens are revoked.</summary>
    public void DisconnectAccount(string collection, RecordId id)
    {
        foreach (var (key, client) in _clients)
        {
            if (client.Principal.Id != id) continue;
            if (!string.Equals(client.Principal.CollectionName, collection, StringComparison.Ordinal)) continue;

            if (_clients.TryRemove(key, out var removed)) removed.Close();
        }
    }

    /// <summary>Broadcasts an event to the subscribers entitled to it.</summary>
    public async Task DispatchAsync(
        RealtimeEvent notification,
        Func<RealtimeEvent, string, string> render,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(render);

        var collection = _registry.Find(notification.Collection);

        if (collection is null) return;

        // The verdict is computed once per distinct caller, not once per subscriber: ten tabs of
        // the same account aren't worth ten evaluations of the same rule.
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

    /// <summary>Does the followed topic designate this collection, or possibly this record?</summary>
    private static bool Matches(string topic, string collection, string? recordId = null)
    {
        if (string.Equals(topic, collection, StringComparison.Ordinal)) return true;

        return recordId is not null
            && string.Equals(topic, $"{collection}/{recordId}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Is the caller allowed to see this record?
    /// </summary>
    /// <remarks>
    /// The rule is the view rule, applied by the record engine itself: reimplementing an
    /// "equivalent" evaluation here would guarantee it drifts one day, and the drift would go in
    /// the direction of a leak.
    /// </remarks>
    private async Task<bool> CanSeeAsync(
        CollectionDefinition collection,
        RealtimeEvent notification,
        RealtimePrincipal principal,
        CancellationToken cancellationToken)
    {
        if (principal.IsSuperuser) return true;

        var rule = collection.Rules.View;

        // Locked: reserved for superusers, already handled above.
        if (rule is null) return false;

        // Open to everyone: nothing to evaluate.
        if (rule.Length == 0) return true;

        // A deletion leaves nothing to evaluate: the row no longer exists, so the rule can no
        // longer be applied. We refuse rather than assume — a subscriber who couldn't see the
        // record shouldn't learn that it disappeared.
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
