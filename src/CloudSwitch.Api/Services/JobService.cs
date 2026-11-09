using CloudSwitch.Api.Authentication;
using CloudSwitch.Api.Contracts;
using CloudSwitch.Api.Infrastructure;
using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Persistence;
using CloudSwitch.Persistence.Repositories;
using Domain = CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Api.Services;

public sealed class JobService(ICurrentAccount accounts, JobRepository jobs, JobSubmissionService submissions)
{
    public async Task<SubmissionResult> SubmitAsync(
        Guid deploymentId, JobRequest request, string idempotencyKey, Guid correlationId, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(cancellationToken);
        return await submissions.SubmitAsync(new SubmitJob(deploymentId, account.Id, request.TargetConfigurationId,
            Domain.JobTrigger.Manual, idempotencyKey, null, correlationId), cancellationToken);
    }

    public async Task<List<JobResponse>> ListAsync(
        Guid? deploymentId, int offset, int limit, CancellationToken cancellationToken)
    {
        ResourceAccess.Page(offset, limit);
        var account = await accounts.GetAsync(cancellationToken);
        var items = await jobs.ListOwnedAsync(account.Id, deploymentId, offset, limit, cancellationToken);
        return items.Select(JobResponse.From).ToList();
    }

    public async Task<JobResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(cancellationToken);
        return JobResponse.From(await FindAsync(id, account.Id, cancellationToken));
    }

    public async Task<List<JobStatusChanged>> ListEventsAsync(
        Guid id, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        if (afterSequence < 0 || limit is < 1 or > 100)
            throw new ApiRequestException(400, "invalid_pagination", "afterSequence must be nonnegative; limit must be between 1 and 100.");
        var account = await accounts.GetAsync(cancellationToken);
        await FindAsync(id, account.Id, cancellationToken);
        var events = await jobs.ListEventsAsync(id, afterSequence, limit, cancellationToken);
        return events.Select(jobEvent => jobEvent.Event.ToContract()).ToList();
    }

    private async Task<Domain.Job> FindAsync(Guid id, Guid userId, CancellationToken cancellationToken)
        => await jobs.FindOwnedAsync(id, userId, cancellationToken)
            ?? throw new ApiRequestException(404, "job_not_found", "Job was not found.");
}
