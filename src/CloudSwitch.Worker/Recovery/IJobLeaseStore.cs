using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;

namespace CloudSwitch.Worker.Recovery;

public interface IJobLeaseStore
{
    Task<JobLease?> TryAcquireAsync(DeploymentRequested request, string workerId, TimeSpan duration,
        int maxAttempts, CancellationToken cancellationToken = default);
    Task<bool> RenewAsync(JobLease lease, TimeSpan duration, CancellationToken cancellationToken = default);
    Task<JobStatusChanged?> RecordProgressAsync(JobLease lease, JobStatus status, MigrationPhase phase,
        CutoverOutcome cutover, Uri? endpoint = null, string? errorCode = null, string? errorMessage = null, string? stateFingerprint = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeploymentRequested>> FindStaleJobsAsync(DateTimeOffset asOf, int limit,
        CancellationToken cancellationToken = default);
}
