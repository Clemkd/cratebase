using Cratebase.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cratebase.Server;

/// <summary>
/// Vide le tampon du journal et applique la rétention.
/// </summary>
/// <remarks>
/// <para>
/// Un service de fond plutôt qu'une tâche planifiée : la fréquence utile — quelques secondes — est
/// hors de portée d'un ordonnanceur, et un travailleur externe supposerait un composant de plus à
/// déployer, ce que la promesse « un seul conteneur » exclut.
/// </para>
/// <para>
/// Aucune panne d'écriture ne doit arrêter la boucle : une base momentanément verrouillée ferait
/// sinon cesser toute journalisation jusqu'au prochain redémarrage, et c'est précisément pendant un
/// incident qu'on a besoin du journal.
/// </para>
/// </remarks>
public sealed partial class LogMaintenanceService(
    LogStore logs,
    SettingsStore settings,
    ILogger<LogMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(3);

    /// <summary>Nombre de passages entre deux purges : une heure.</summary>
    private const int TicksBetweenPurges = 1_200;

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Entretien du journal impossible : {Reason}")]
    private static partial void LogMaintenanceFailed(ILogger logger, string reason);

    private readonly LogStore _logs = logs ?? throw new ArgumentNullException(nameof(logs));

    private readonly SettingsStore _settings = settings
        ?? throw new ArgumentNullException(nameof(settings));

    private readonly ILogger<LogMaintenanceService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Une purge dès le démarrage : une instance arrêtée plusieurs jours reprend avec un journal
        // déjà hors rétention, et attendre une heure pour l'apurer afficherait à l'administrateur
        // des entrées que les réglages disent supprimées.
        await SafelyAsync(() => _logs.PurgeAsync(_settings.Current.Logs.RetentionDays, stoppingToken))
            .ConfigureAwait(false);

        using var timer = new PeriodicTimer(FlushInterval);
        var ticks = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SafelyAsync(() => _logs.FlushAsync(stoppingToken)).ConfigureAwait(false);

                if (++ticks < TicksBetweenPurges)
                {
                    continue;
                }

                ticks = 0;

                await SafelyAsync(() => _logs.PurgeAsync(_settings.Current.Logs.RetentionDays, stoppingToken))
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé : le dernier lot part dans StopAsync.
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Sans ce dernier passage, les quelques secondes de requêtes précédant l'arrêt seraient
        // perdues — dont, potentiellement, celles qui expliquent pourquoi on a redémarré.
        await SafelyAsync(() => _logs.FlushAsync(cancellationToken)).ConfigureAwait(false);
    }

    private async Task SafelyAsync(Func<Task<int>> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            LogMaintenanceFailed(_logger, failure.Message);
        }
    }
}
