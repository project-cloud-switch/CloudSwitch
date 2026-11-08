using CloudSwitch.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace CloudSwitch.Persistence.Repositories;

public sealed class JobRepository(CloudSwitchDbContext db)
{
    public Task<Job?> FindOwnedAsync(Guid id, Guid userId, CancellationToken cancellationToken)
        => db.Jobs.AsNoTracking().SingleOrDefaultAsync(
            job => job.Id == id && job.UserId == userId, cancellationToken);

    public Task<List<Job>> ListOwnedAsync(
        Guid userId, Guid? deploymentId, int offset, int limit, CancellationToken cancellationToken)
        => db.Jobs.AsNoTracking()
            .Where(job => job.UserId == userId && (deploymentId == null || job.DeploymentId == deploymentId))
            .OrderByDescending(job => job.CreatedAt)
            .ThenBy(job => job.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<bool> HasActiveAsync(Guid deploymentId, CancellationToken cancellationToken)
        => db.Jobs.AnyAsync(job => job.DeploymentId == deploymentId
            && (job.Status == JobStatus.Pending || job.Status == JobStatus.Running), cancellationToken);

    public Task<Job?> FindPreviousSubmissionAsync(
        Guid deploymentId, JobTrigger trigger, string? idempotencyKey, Guid? incidentId,
        CancellationToken cancellationToken)
        => db.Jobs.SingleOrDefaultAsync(job => job.DeploymentId == deploymentId
            && (trigger == JobTrigger.Manual
                ? job.IdempotencyKey == idempotencyKey
                : job.IncidentId == incidentId && job.Trigger == JobTrigger.Automatic), cancellationToken);

    public Task<List<JobEvent>> ListEventsAsync(
        Guid jobId, long afterSequence, int limit, CancellationToken cancellationToken)
        => db.JobEvents.AsNoTracking()
            .Where(jobEvent => jobEvent.JobId == jobId && jobEvent.Sequence > afterSequence)
            .OrderBy(jobEvent => jobEvent.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public void Add(Job job)
        => db.Jobs.Add(job);
}
