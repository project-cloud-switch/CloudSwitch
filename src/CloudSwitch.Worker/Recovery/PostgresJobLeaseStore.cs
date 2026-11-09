using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Domain = CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Worker.Recovery;

public sealed class PostgresJobLeaseStore(IServiceScopeFactory scopes) : IJobLeaseStore
{
    public async Task<JobLease?> TryAcquireAsync(DeploymentRequested request, string workerId, TimeSpan duration,
        int maxAttempts, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudSwitchDbContext>();
        await using var transaction = await DeploymentTransactions.BeginAsync(db, request.DeploymentId, cancellationToken);
        var job = await db.Jobs.SingleOrDefaultAsync(job => job.Id == request.JobId, cancellationToken)
            ?? throw new InvalidDataException("The requested job does not exist.");
        if (job.DeploymentId != request.DeploymentId || job.UserId != request.UserId
            || job.Request.EventId != request.EventId || job.Request.CorrelationId != request.CorrelationId)
            throw new InvalidDataException("Message identity does not match the persisted job.");
        var now = await DatabaseTimeAsync(db, cancellationToken);
        if (!job.CanAcquireLease(now))
            return null;
        if (job.Attempt >= maxAttempts)
        {
            var uncertain = job.Cutover is Domain.CutoverOutcome.Applied or Domain.CutoverOutcome.Uncertain;
            var progress = job.RecordProgress(Domain.JobStatus.Failed,
                uncertain ? Domain.MigrationPhase.TrafficCutover : job.Phase,
                uncertain ? Domain.CutoverOutcome.Uncertain : job.Cutover,
                job.TargetEndpoint, "RETRY_LIMIT_EXHAUSTED", "The job requires manual recovery after exhausting its retry limit.", now);
            await SaveProgressAsync(scope.ServiceProvider, db, progress, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var previous = job.Status;
        job.AcquireLease(workerId, duration, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new JobLease(job.Request.ToContract(), workerId, job.Attempt, job.FencingToken,
            now.Add(duration), ContractMapping.ConvertEnum<JobStatus>(previous), job.Sequence,
            job.Phase.ToContract(), job.TargetEndpoint,
            ContractMapping.ConvertEnum<CutoverOutcome>(job.Cutover), job.Checkpoints.StateFingerprint);
    }

    public async Task<bool> RenewAsync(JobLease lease, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudSwitchDbContext>();
        await using var transaction = await DeploymentTransactions.BeginAsync(db, lease.DeploymentId, cancellationToken);
        var job = await db.Jobs.SingleAsync(job => job.Id == lease.JobId, cancellationToken);
        var now = await DatabaseTimeAsync(db, cancellationToken);
        if (!job.OwnsLease(lease.WorkerId, lease.FencingToken, now))
            return false;
        job.RenewLease(duration, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<JobStatusChanged?> RecordProgressAsync(JobLease lease, JobStatus status, MigrationPhase phase,
        CutoverOutcome cutover, Uri? endpoint = null, string? errorCode = null, string? errorMessage = null, string? stateFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudSwitchDbContext>();
        await using var transaction = await DeploymentTransactions.BeginAsync(db, lease.DeploymentId, cancellationToken);
        var job = await db.Jobs.SingleAsync(job => job.Id == lease.JobId, cancellationToken);
        var now = await DatabaseTimeAsync(db, cancellationToken);
        if (!job.OwnsLease(lease.WorkerId, lease.FencingToken, now))
            return null;
        if (status == JobStatus.Succeeded)
        {
            var migration = job.Request.Specification.Migration
                ?? throw new InvalidDataException("A complete migration snapshot is required.");
            var runtime = await db.Runtimes.SingleOrDefaultAsync(runtime => runtime.DeploymentId == job.DeploymentId, cancellationToken);
            if ((runtime?.Revision ?? 0) != migration.Source.RuntimeRevision)
                throw new InvalidOperationException("The active runtime changed during migration.");
            ArgumentNullException.ThrowIfNull(endpoint);
            if (runtime is null)
                db.Runtimes.Add(Domain.DeploymentRuntime.Activate(job.DeploymentId, migration.Target.Provider, endpoint, 1, now));
            else
                runtime.Activate(migration.Target.Provider, endpoint, now);
        }
        var previous = job.Sequence == lease.Sequence ? ContractMapping.ConvertEnum<Domain.JobStatus>(lease.PreviousStatus) : job.Status;
        var progress = job.RecordProgress(ContractMapping.ConvertEnum<Domain.JobStatus>(status),
            phase.ToDomain(), ContractMapping.ConvertEnum<Domain.CutoverOutcome>(cutover),
            endpoint, errorCode, errorMessage, now, previous, stateFingerprint);
        await SaveProgressAsync(scope.ServiceProvider, db, progress, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return progress.ToContract();
    }

    public async Task<IReadOnlyList<DeploymentRequested>> FindStaleJobsAsync(DateTimeOffset asOf, int limit,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudSwitchDbContext>();
        var now = await DatabaseTimeAsync(db, cancellationToken);
        var jobs = await db.Jobs.AsNoTracking().Where(job => (job.Status == Domain.JobStatus.Pending || job.Status == Domain.JobStatus.Running) && job.LeaseExpiresAt <= now)
            .OrderBy(job => job.LeaseExpiresAt).ThenBy(job => job.Id).Take(limit).ToListAsync(cancellationToken);
        return jobs.Select(job => job.Request.ToContract()).ToList();
    }

    private static Task<DateTimeOffset> DatabaseTimeAsync(CloudSwitchDbContext db, CancellationToken cancellationToken)
        => db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);

    private static async Task SaveProgressAsync(IServiceProvider services, CloudSwitchDbContext db,
        Domain.JobProgress progress, CancellationToken cancellationToken)
    {
        db.JobEvents.Add(Domain.JobEvent.Record(progress));
        await services.GetRequiredService<IPublishEndpoint>().Publish(progress.ToContract(), publish =>
        {
            publish.MessageId = progress.EventId;
            publish.CorrelationId = progress.CorrelationId;
        }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}
