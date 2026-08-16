using System.Text.Json;
using Cratebase.Admin;
using Cratebase.Core;
using Cratebase.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cratebase.Server;

/// <summary>Topics followed by a client, as it declares them.</summary>
public sealed record SubscriptionRequest
{
    /// <summary>Identifier handed out at connection.</summary>
    public string? ClientId { get; init; }

    /// <summary>Topics: <c>collection</c> or <c>collection/id</c>.</summary>
    public IReadOnlyList<string>? Subscriptions { get; init; }
}

/// <summary>
/// Realtime: one event stream per client, filtered by the access rules.
/// </summary>
/// <remarks>
/// <para>
/// Two routes, like PocketBase: <c>GET /api/realtime</c> opens the stream and returns a client
/// identifier, <c>POST /api/realtime</c> declares what that client follows. The split isn't a
/// stylistic choice: a browser can't set a header on an event source, so the subscription — which
/// does carry the token — must be a separate request.
/// </para>
/// <para>
/// <b>SSE, not WebSocket.</b> The need is one-directional: the server pushes, the client listens.
/// SSE passes through proxies and load balancers with no negotiation, reconnects on its own, and
/// runs over ordinary HTTP. A WebSocket would bring an upstream channel nothing here uses, against
/// one more stack to operate.
/// </para>
/// </remarks>
public static class RealtimeEndpoints
{
    /// <summary>Interval between keep-alive comments, in seconds.</summary>
    private const int HeartbeatSeconds = 25;

    /// <summary>Publishes the realtime routes.</summary>
    public static IEndpointRouteBuilder MapRealtimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/realtime");

        group.MapGet("/", async (
            HttpContext http,
            RealtimeHub hub,
            SettingsStore settings,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (!settings.Current.Realtime.Enabled)
            {
                throw new CratebaseBadRequestException(
                    "Realtime is disabled on this instance.");
            }

            if (hub.Count >= settings.Current.Realtime.MaxClients)
            {
                throw new CratebaseConflictException(
                    $"Too many open streams ({hub.Count}). Close one or raise the limit in "
                    + "Administration → Settings.");
            }

            var client = hub.Connect(user);

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            // Without this header, a proxy that buffers the response holds events until its buffer
            // is full: the stream works and never arrives.
            http.Response.Headers["X-Accel-Buffering"] = "no";

            await WriteFrameAsync(http, "connect", $"{{\"clientId\":\"{client.Id}\"}}", cancellationToken)
                .ConfigureAwait(false);

            try
            {
                // The keep-alive isn't a courtesy: with no traffic, a proxy closes an idle
                // connection after about a minute, and the client spends its time reconnecting
                // without ever understanding why.
                using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(HeartbeatSeconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, http.RequestAborted);

                var beating = BeatAsync(http, heartbeat, linked.Token);

                await foreach (var payload in client.ReadAsync(linked.Token).ConfigureAwait(false))
                {
                    await http.Response.WriteAsync(payload, linked.Token).ConfigureAwait(false);
                    await http.Response.Body.FlushAsync(linked.Token).ConfigureAwait(false);
                }

                await beating.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Client left: this is the normal end of a stream, not an anomaly.
            }
            finally
            {
                hub.Disconnect(client.Id);
            }
        });

        group.MapPost("/", (
            SubscriptionRequest request,
            RealtimeHub hub,
            ICurrentUser user) =>
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(request.ClientId))
            {
                throw new CratebaseBadRequestException("Missing client identifier.");
            }

            if (!hub.Subscribe(request.ClientId, request.Subscriptions ?? [], user))
            {
                throw new CratebaseNotFoundException("This stream is no longer open.");
            }

            return Results.NoContent();
        });

        return endpoints;
    }

    /// <summary>Wraps an event into an SSE frame.</summary>
    /// <remarks>
    /// The event name carries the followed topic: the client subscribes to <c>posts</c> and listens
    /// for <c>posts</c>, without having to demultiplex a single stream itself.
    /// </remarks>
    internal static string Frame(string name, string data) =>
        $"event: {name}\ndata: {data}\n\n";

    private static async Task WriteFrameAsync(
        HttpContext http,
        string name,
        string data,
        CancellationToken cancellationToken)
    {
        await http.Response.WriteAsync(Frame(name, data), cancellationToken).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task BeatAsync(
        HttpContext http,
        PeriodicTimer timer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // An SSE comment: the client ignores it, the proxy sees traffic.
                await http.Response.WriteAsync(":\n\n", cancellationToken).ConfigureAwait(false);
                await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stream closed.
        }
    }
}

/// <summary>
/// Routes events from the transport to subscribers.
/// </summary>
/// <remarks>
/// A single reader for the whole process, not one per stream: it's what applies the access rules
/// once per distinct caller, where N readers would apply them N times. It lives in the host rather
/// than in <c>Cratebase.Realtime</c> because it reads settings, which belong to operations.
/// </remarks>
public sealed partial class RealtimeDispatcher(
    IRealtimeTransport transport,
    RealtimeHub hub,
    SettingsStore settings,
    ILogger<RealtimeDispatcher> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Payload = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notification in transport.ReadAsync(stoppingToken).ConfigureAwait(false))
        {
            // The setting is read on every event, not at startup: turning it off from the console
            // must interrupt broadcasting without a restart, and streams already open then go quiet
            // instead of being cut.
            if (!settings.Current.Realtime.Enabled) continue;

            try
            {
                await hub.DispatchAsync(notification, Render, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                Failed(logger, notification.Collection, failure);
            }
        }
    }

    private static string Render(RealtimeEvent notification, string topic) =>
        RealtimeEndpoints.Frame(
            topic,
            JsonSerializer.Serialize(
                new
                {
                    action = notification.Action.ToString().ToLowerInvariant(),
                    collection = notification.Collection,
                    id = notification.RecordId,
                    record = notification.Record,
                },
                Payload));

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Realtime broadcast interrupted for collection {Collection}.")]
    private static partial void Failed(ILogger logger, string collection, Exception error);
}
