using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using MassTransit;

namespace CloudSwitch.Worker.Recovery;

/// <summary>Process-local test implementation. The executable host uses PostgreSQL.</summary>
public sealed class InMemoryJobLeaseStore(IPublishEndpoint publisher, TimeProvider? timeProvider = null) : IJobLeaseStore
{
    private sealed record State(DeploymentRequested Request, int Attempt, long Fence, long Sequence,
        JobStatus Status, MigrationPhase Phase, CutoverOutcome Cutover, Uri? Endpoint,
        string? Owner, DateTimeOffset? ExpiresAt, string? Fingerprint);

    private readonly Dictionary<Guid, State> jobs = [];
    private readonly object sync = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public void RegisterJob(DeploymentRequested request, int attempt = 0, long sequence = 0,
        JobStatus status = JobStatus.Pending, MigrationPhase phase = MigrationPhase.Pending,
        CutoverOutcome cutover = CutoverOutcome.NotStarted, Uri? endpoint = null,
        DateTimeOffset? expiresAt = null, string? owner = null, string? stateFingerprint = null)
    {
        lock (sync)
            jobs.Add(request.JobId, new State(request, attempt, attempt, sequence, status, phase, cutover, endpoint, owner, expiresAt, stateFingerprint));
    }

    public async Task<JobLease?> TryAcquireAsync(DeploymentRequested request, string workerId, TimeSpan duration,
        int maxAttempts, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JobStatusChanged? exhausted = null;
        JobLease? lease = null;
        lock (sync)
        {
            var job = jobs[request.JobId];
            if (job.Request.EventId != request.EventId || job.Request.UserId != request.UserId
                || job.Request.DeploymentId != request.DeploymentId || job.Request.CorrelationId != request.CorrelationId)
                throw new InvalidDataException("Message identity does not match the persisted job.");
            if (job.Status is JobStatus.Succeeded or JobStatus.Failed || job.ExpiresAt > clock.GetUtcNow())
                return null;
            if (job.Attempt >= maxAttempts)
            {
                exhausted = Progress(job, JobStatus.Failed, job.Phase, job.Cutover, job.Endpoint,
                    "RETRY_LIMIT_EXHAUSTED", "The job requires manual recovery after exhausting its retry limit.");
                jobs[request.JobId] = job with { Status = JobStatus.Failed, Sequence = exhausted.Sequence };
            }
            else
            {
                var expires = clock.GetUtcNow().Add(duration);
                lease = new JobLease(job.Request, workerId, job.Attempt + 1, job.Fence + 1, expires,
                    job.Status, job.Sequence, job.Phase, job.Endpoint, job.Cutover, job.Fingerprint);
                jobs[request.JobId] = job with
                {
                    Attempt = lease.Attempt, Fence = lease.FencingToken, Owner = workerId,
                    ExpiresAt = expires
                };
            }
        }
        if (exhausted is not null)
            await publisher.Publish(exhausted, cancellationToken);
        return lease;
    }

    public Task<bool> RenewAsync(JobLease lease, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var job = jobs[lease.JobId];
            if (!Owns(job, lease))
                return Task.FromResult(false);
            jobs[lease.JobId] = job with { ExpiresAt = clock.GetUtcNow().Add(duration) };
            return Task.FromResult(true);
        }
    }

    public async Task<JobStatusChanged?> RecordProgressAsync(JobLease lease, JobStatus status, MigrationPhase phase,
        CutoverOutcome cutover, Uri? endpoint = null, string? errorCode = null, string? errorMessage = null, string? stateFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JobStatusChanged progress;
        lock (sync)
        {
            var job = jobs[lease.JobId];
            if (!Owns(job, lease))
                return null;
            var previous = job.Sequence == lease.Sequence ? lease.PreviousStatus : job.Status;
            progress = Progress(job, status, phase, cutover, endpoint ?? job.Endpoint, errorCode, errorMessage) with { PreviousStatus = previous };
            jobs[lease.JobId] = job with
            {
                Status = status, Phase = phase, Cutover = cutover, Endpoint = endpoint ?? job.Endpoint,
                Sequence = progress.Sequence, Fingerprint = stateFingerprint ?? job.Fingerprint, ExpiresAt = status == JobStatus.Running ? job.ExpiresAt : null
            };
        }
        await publisher.Publish(progress, cancellationToken);
        return progress;
    }

    public Task<IReadOnlyList<DeploymentRequested>> FindStaleJobsAsync(DateTimeOffset asOf, int limit,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
            return Task.FromResult<IReadOnlyList<DeploymentRequested>>(jobs.Values
                .Where(job => (job.Status == JobStatus.Pending || job.Status == JobStatus.Running) && job.ExpiresAt <= asOf)
                .OrderBy(job => job.ExpiresAt).Take(limit).Select(job => job.Request).ToList());
    }

    private bool Owns(State job, JobLease lease)
        => job.Status is JobStatus.Pending or JobStatus.Running && job.Owner == lease.WorkerId && job.Fence == lease.FencingToken
            && job.ExpiresAt > clock.GetUtcNow();

    private JobStatusChanged Progress(State job, JobStatus status, MigrationPhase phase, CutoverOutcome cutover,
        Uri? endpoint, string? code, string? message) => new()
        {
            EventId = Guid.NewGuid(), CorrelationId = job.Request.CorrelationId, OccurredAt = clock.GetUtcNow(),
            JobId = job.Request.JobId, DeploymentId = job.Request.DeploymentId, UserId = job.Request.UserId,
            PreviousStatus = job.Status, Status = status, Phase = phase, Cutover = cutover,
            Attempt = job.Attempt, Sequence = job.Sequence + 1, TargetEndpoint = endpoint?.ToString(),
            ErrorCode = code, ErrorMessage = message
        };
}
