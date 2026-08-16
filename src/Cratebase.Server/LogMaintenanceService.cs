using Cratebase.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cratebase.Server;

/// <summary>
/// Flushes the log buffer and applies retention.
/// </summary>
/// <remarks>
/// <para>
/// A background service rather than a scheduled task: the useful frequency — a few seconds — is
/// out of reach for a scheduler, and an external worker would assume one more component to deploy,
/// which the "single container" promise rules out.
/// </para>
/// <para>
/// No write failure should stop the loop: a momentarily locked database would otherwise halt all
/// logging until the next restart, and it's precisely during an incident that the log is needed.
/// </para>
/// </remarks>
public sealed partial class LogMaintenanceService(
    LogStore logs,
    SettingsStore settings,
    ILogger<LogMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(3);

    /// <summary>Number of ticks between two purges: one hour.</summary>
    private const int TicksBetweenPurges = 1_200;

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Log maintenance failed: {Reason}")]
    private static partial void LogMaintenanceFailed(ILogger logger, string reason);

    private readonly LogStore _logs = logs ?? throw new ArgumentNullException(nameof(logs));

    private readonly SettingsStore _settings = settings
        ?? throw new ArgumentNullException(nameof(settings));

    private readonly ILogger<LogMaintenanceService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A purge right at startup: an instance stopped for several days resumes with a log
        // already past retention, and waiting an hour to clean it up would show the administrator
        // entries the settings say are deleted.
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
            // Shutdown requested: the last batch is flushed in StopAsync.
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Without this last pass, the few seconds of requests preceding shutdown would be lost —
        // potentially including the ones that explain why a restart was needed.
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
