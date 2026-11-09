using CloudSwitch.Worker.Orchestration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudSwitch.Worker.Recovery;

public sealed class JobRecoveryService(IJobLeaseStore store, IMigrationOrchestrator orchestrator,
    IOptions<WorkerOptions> options, ILogger<JobRecoveryService> logger) : BackgroundService
{
    public async Task RecoverStaleJobsAsync(CancellationToken cancellationToken = default)
    {
        var requests = await store.FindStaleJobsAsync(DateTimeOffset.UtcNow, 100, cancellationToken);
        foreach (var request in requests)
            await orchestrator.ExecuteMigrationAsync(request, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.RecoveryIntervalSeconds));
        do
        {
            try
            {
                await RecoverStaleJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                logger.LogError("Stale job recovery failed; it will retry on the next scan.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
