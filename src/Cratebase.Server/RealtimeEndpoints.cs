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

/// <summary>Sujets suivis par un client, tels qu'il les déclare.</summary>
public sealed record SubscriptionRequest
{
    /// <summary>Identifiant remis à la connexion.</summary>
    public string? ClientId { get; init; }

    /// <summary>Sujets : <c>collection</c> ou <c>collection/identifiant</c>.</summary>
    public IReadOnlyList<string>? Subscriptions { get; init; }
}

/// <summary>
/// Temps réel : un flux d'évènements par client, filtré par les règles d'accès.
/// </summary>
/// <remarks>
/// <para>
/// Deux routes, comme chez PocketBase : <c>GET /api/realtime</c> ouvre le flux et rend un
/// identifiant de client, <c>POST /api/realtime</c> déclare ce que ce client suit. La séparation
/// n'est pas une coquetterie : un navigateur ne peut pas poser d'en-tête sur une source
/// d'évènements, donc l'abonnement — qui, lui, porte le jeton — doit être une requête distincte.
/// </para>
/// <para>
/// <b>SSE et non WebSocket.</b> Le besoin est unidirectionnel : le serveur pousse, le client
/// écoute. SSE passe les mandataires et les répartiteurs sans négociation, se reconnecte tout seul,
/// et tient sur du HTTP ordinaire. Un WebSocket apporterait un canal montant dont rien ici ne se
/// sert, contre une pile de plus à exploiter.
/// </para>
/// </remarks>
public static class RealtimeEndpoints
{
    /// <summary>Intervalle des commentaires de maintien, en secondes.</summary>
    private const int HeartbeatSeconds = 25;

    /// <summary>Publie les routes du temps réel.</summary>
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
                    "Le temps réel est désactivé sur cette instance.");
            }

            if (hub.Count >= settings.Current.Realtime.MaxClients)
            {
                throw new CratebaseConflictException(
                    $"Trop de flux ouverts ({hub.Count}). Fermez-en un ou relevez la limite dans "
                    + "Administration → Paramètres.");
            }

            var client = hub.Connect(user);

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            // Sans cet en-tête, un mandataire qui tamponne la réponse retient les évènements
            // jusqu'à ce que son tampon soit plein : le flux fonctionne et n'arrive jamais.
            http.Response.Headers["X-Accel-Buffering"] = "no";

            await WriteFrameAsync(http, "connect", $"{{\"clientId\":\"{client.Id}\"}}", cancellationToken)
                .ConfigureAwait(false);

            try
            {
                // Le maintien n'est pas une politesse : sans trafic, un mandataire ferme une
                // connexion inactive au bout d'une minute, et le client passe son temps à se
                // reconnecter sans jamais comprendre pourquoi.
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
                // Départ du client : c'est la fin normale d'un flux, pas une anomalie.
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
                throw new CratebaseBadRequestException("Identifiant de client manquant.");
            }

            if (!hub.Subscribe(request.ClientId, request.Subscriptions ?? [], user))
            {
                throw new CratebaseNotFoundException("Ce flux n'est plus ouvert.");
            }

            return Results.NoContent();
        });

        return endpoints;
    }

    /// <summary>Met un évènement en trame SSE.</summary>
    /// <remarks>
    /// Le nom d'évènement porte le sujet suivi : le client s'abonne à <c>posts</c> et écoute
    /// <c>posts</c>, sans avoir à démultiplexer lui-même un flux unique.
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
                // Un commentaire SSE : le client l'ignore, le mandataire y voit du trafic.
                await http.Response.WriteAsync(":\n\n", cancellationToken).ConfigureAwait(false);
                await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Flux fermé.
        }
    }
}

/// <summary>
/// Achemine les évènements du transport vers les abonnés.
/// </summary>
/// <remarks>
/// Un seul lecteur pour tout le processus, et non un par flux : c'est lui qui applique les règles
/// d'accès une fois par appelant distinct, là où N lecteurs les appliqueraient N fois. Il vit dans
/// l'hôte et non dans <c>Cratebase.Realtime</c> parce qu'il consulte les réglages, qui appartiennent
/// à l'exploitation.
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
            // Le réglage est lu à chaque évènement, pas au démarrage : le fermer depuis la console
            // doit interrompre la diffusion sans redémarrage, et les flux déjà ouverts se taisent
            // alors au lieu d'être coupés.
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
        Message = "Diffusion temps réel interrompue pour la collection {Collection}.")]
    private static partial void Failed(ILogger logger, string collection, Exception error);
}
