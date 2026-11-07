using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Orchestration;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudSwitch.Worker.Recovery;

public sealed record RecoveryExecutionResult
{
    public required Guid JobId { get; init; }
    public required bool Recovered { get; init; }
    public string? Reason { get; init; }
    public MigrationOutcome? Outcome { get; init; }
}

/// <summary>
/// Discovers stale running jobs whose leases expired, fences out old workers,
/// reconciles actual Terraform and routing state, and safely resumes execution.
/// </summary>
public sealed class JobRecoveryService
{
    private readonly IJobLeaseStore _leaseStore;
    private readonly IInfrastructureExecutor _executor;
    private readonly IMigrationOrchestrator _orchestrator;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<JobRecoveryService> _logger;

    public JobRecoveryService(
        IJobLeaseStore leaseStore,
        IInfrastructureExecutor executor,
        IMigrationOrchestrator orchestrator,
        IPublishEndpoint publishEndpoint,
        ILogger<JobRecoveryService>? logger = null)
    {
        _leaseStore = leaseStore;
        _executor = executor;
        _orchestrator = orchestrator;
        _publishEndpoint = publishEndpoint;
        _logger = logger ?? NullLogger<JobRecoveryService>.Instance;
    }

    public async Task<IReadOnlyList<RecoveryExecutionResult>> RecoverStaleJobsAsync(
        string recoveryWorkerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var staleJobs = await _leaseStore.FindStaleJobsAsync(now, cancellationToken).ConfigureAwait(false);
        var results = new List<RecoveryExecutionResult>();

        foreach (var job in staleJobs)
        {
            _logger.LogInformation("Attempting recovery for stale JobId={JobId}, Attempt={Attempt}", job.JobId, job.Attempt);

            var lease = await _leaseStore.TryAcquireOrRenewLeaseAsync(job.JobId, recoveryWorkerId, leaseDuration, cancellationToken).ConfigureAwait(false);
            if (lease == null)
            {
                results.Add(new RecoveryExecutionResult
                {
                    JobId = job.JobId,
                    Recovered = false,
                    Reason = job.HasExhaustedRetries
                        ? "RETRY_LIMIT_EXHAUSTED"
                        : "Lease could not be acquired (taken by another worker or retry limit reached)."
                });
                continue;
            }

            // Reconcile actual target infrastructure state before repeating external calls
            var reconcileResult = await _executor.ReconcileAsync(new ReconciliationRequest
            {
                DeploymentId = lease.DeploymentId,
                Provider = lease.Specification.Provider,
                TerraformModule = lease.Specification.TerraformModule ?? "container-vm-selectel:v1.0.0",
                ExpectedTargetEndpoint = lease.TargetEndpoint
            }, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Job {JobId} reconciliation: Exists={Exists}, ObservedEndpoint={Endpoint}",
                lease.JobId, reconcileResult.Exists, reconcileResult.ObservedTargetEndpoint);

            var deploymentRequest = new DeploymentRequested
            {
                EventId = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow,
                JobId = lease.JobId,
                DeploymentId = lease.DeploymentId,
                UserId = lease.UserId,
                Trigger = DeploymentTrigger.Automatic,
                Specification = lease.Specification
            };

            var outcome = await _orchestrator.ExecuteMigrationAsync(deploymentRequest, cancellationToken).ConfigureAwait(false);

            if (outcome.IsSuccess)
            {
                await _leaseStore.MarkTerminalAsync(
                    lease.JobId, lease.Attempt, JobStatus.Succeeded, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _leaseStore.MarkTerminalAsync(
                    lease.JobId, lease.Attempt, JobStatus.Failed, outcome.ErrorCode, outcome.ErrorMessage, cancellationToken).ConfigureAwait(false);
            }

            results.Add(new RecoveryExecutionResult
            {
                JobId = lease.JobId,
                Recovered = outcome.IsSuccess,
                Outcome = outcome
            });
        }

        return results;
    }
}
