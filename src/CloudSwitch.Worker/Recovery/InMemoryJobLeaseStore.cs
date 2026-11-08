using CloudSwitch.Contracts;

namespace CloudSwitch.Worker.Recovery;

/// <summary>
/// In-memory lease store implementing atomic lease acquisition, fenced checkpoint updates,
/// and stale job discovery for testing and local simulation.
/// </summary>
public sealed class InMemoryJobLeaseStore : IJobLeaseStore
{
    private readonly Dictionary<Guid, JobLease> _leases = new();
    private readonly object _syncRoot = new();

    public void RegisterJob(JobLease lease)
    {
        lock (_syncRoot)
        {
            _leases[lease.JobId] = lease;
        }
    }

    public Task<JobLease?> TryAcquireOrRenewLeaseAsync(
        Guid jobId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (!_leases.TryGetValue(jobId, out var existing))
                return Task.FromResult<JobLease?>(null);

            var now = DateTimeOffset.UtcNow;

            // Terminal jobs cannot be leased
            if (existing.Status == JobStatus.Succeeded || existing.Status == JobStatus.Failed)
                return Task.FromResult<JobLease?>(null);

            // Active valid lease held by someone else cannot be stolen
            if (!existing.IsExpired(now) && existing.WorkerId != workerId)
                return Task.FromResult<JobLease?>(null);

            var isNewAttempt = existing.WorkerId != workerId && existing.IsExpired(now);
            var attempt = isNewAttempt ? existing.Attempt + 1 : existing.Attempt;

            // Check retry limit
            if (attempt > existing.MaxAttempts)
            {
                var exhausted = existing with
                {
                    Status = JobStatus.Failed,
                    ErrorCode = "RETRY_LIMIT_EXHAUSTED",
                    ErrorMessage = $"Job exceeded maximum retry attempts ({existing.MaxAttempts}). Manual recovery required."
                };
                _leases[jobId] = exhausted;
                return Task.FromResult<JobLease?>(null);
            }

            var updated = existing with
            {
                WorkerId = workerId,
                Attempt = attempt,
                Status = JobStatus.Running,
                LeaseExpiresAt = now.Add(leaseDuration)
            };

            _leases[jobId] = updated;
            return Task.FromResult<JobLease?>(updated);
        }
    }

    public Task<IReadOnlyList<JobLease>> FindStaleJobsAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            var stale = _leases.Values
                .Where(j => j.Status == JobStatus.Running && j.IsExpired(asOf))
                .ToList();

            return Task.FromResult<IReadOnlyList<JobLease>>(stale);
        }
    }

    public Task<bool> UpdateCheckpointAsync(
        Guid jobId,
        int expectedAttempt,
        MigrationPhase phase,
        string? targetEndpoint = null,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (!_leases.TryGetValue(jobId, out var existing))
                return Task.FromResult(false);

            // Fencing: reject writes from an expired attempt
            if (existing.Attempt != expectedAttempt)
                return Task.FromResult(false);

            var updated = existing with
            {
                CurrentPhase = phase,
                TargetEndpoint = targetEndpoint ?? existing.TargetEndpoint
            };

            _leases[jobId] = updated;
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkTerminalAsync(
        Guid jobId,
        int expectedAttempt,
        JobStatus status,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (!_leases.TryGetValue(jobId, out var existing))
                return Task.FromResult(false);

            if (existing.Attempt != expectedAttempt)
                return Task.FromResult(false);

            var updated = existing with
            {
                Status = status,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            };

            _leases[jobId] = updated;
            return Task.FromResult(true);
        }
    }
}
