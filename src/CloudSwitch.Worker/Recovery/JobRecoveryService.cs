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
        if (requests.Count == 0)
            return;

        var maxConcurrency = Math.Max(1, options.Value.MaxConcurrentRecoveries);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = requests.Select(async request =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await orchestrator.ExecuteMigrationAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Isolated recovery failure for JobId={JobId}, DeploymentId={DeploymentId}. Continuing recovery scan.",
                    request.JobId, request.DeploymentId);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
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
