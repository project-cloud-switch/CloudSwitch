using CloudSwitch.Contracts;

namespace CloudSwitch.Worker.Recovery;

/// <summary>
/// Abstraction for database lease locking, fenced ownership checkpoints,
/// and stale job discovery.
/// </summary>
public interface IJobLeaseStore
{
    Task<JobLease?> TryAcquireOrRenewLeaseAsync(
        Guid jobId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JobLease>> FindStaleJobsAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateCheckpointAsync(
        Guid jobId,
        int expectedAttempt,
        MigrationPhase phase,
        string? targetEndpoint = null,
        CancellationToken cancellationToken = default);

    Task<bool> MarkTerminalAsync(
        Guid jobId,
        int expectedAttempt,
        JobStatus status,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);
}
