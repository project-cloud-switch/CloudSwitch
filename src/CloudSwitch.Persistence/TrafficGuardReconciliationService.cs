using CloudSwitch.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace CloudSwitch.Persistence;

public interface ITrafficGuardReconciliationService
{
    Task<TrafficGuard> ReconcileAndClearGuardAsync(
        Guid deploymentId,
        string clearedBy,
        string evidence,
        CancellationToken ct = default);

    Task<TrafficGuard?> FindActiveOrUncertainGuardAsync(
        Guid deploymentId,
        CancellationToken ct = default);
}

public sealed class TrafficGuardReconciliationService(CloudSwitchDbContext db) : ITrafficGuardReconciliationService
{
    public async Task<TrafficGuard> ReconcileAndClearGuardAsync(
        Guid deploymentId,
        string clearedBy,
        string evidence,
        CancellationToken ct = default)
    {
        if (deploymentId == Guid.Empty)
            throw new ArgumentException("DeploymentId must be a valid UUID.", nameof(deploymentId));
        if (string.IsNullOrWhiteSpace(clearedBy))
            throw new ArgumentException("Authorized operator identity is required.", nameof(clearedBy));
        if (string.IsNullOrWhiteSpace(evidence) || evidence.Trim().Length < 10)
            throw new ArgumentException("Verified evidence of manual reconciliation (at least 10 characters) is required.", nameof(evidence));

        await using var transaction = await DeploymentTransactions.BeginAsync(db, deploymentId, ct);

        var guard = await db.TrafficGuards
            .Where(g => g.DeploymentId == deploymentId && (g.State == TrafficGuardState.Active || g.State == TrafficGuardState.Uncertain))
            .OrderByDescending(g => g.CreatedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new OperationRejectedException(RejectionKind.NotFound, "guard_not_found", "No active or uncertain traffic guard exists for this deployment.");

        // Check if the associated job is still actively running with a valid lease
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == guard.ActiveJobId, ct);
        var now = DateTimeOffset.UtcNow;
        if (job is not null && job.Status is JobStatus.Pending or JobStatus.Running && job.LeaseExpiresAt > now)
        {
            throw new OperationRejectedException(RejectionKind.Conflict, "job_still_leased",
                "Cannot clear traffic guard while the associated migration job has an active worker lease.");
        }

        guard.Clear(clearedBy, evidence, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return guard;
    }

    public async Task<TrafficGuard?> FindActiveOrUncertainGuardAsync(
        Guid deploymentId,
        CancellationToken ct = default)
    {
        return await db.TrafficGuards
            .AsNoTracking()
            .Where(g => g.DeploymentId == deploymentId && (g.State == TrafficGuardState.Active || g.State == TrafficGuardState.Uncertain))
            .OrderByDescending(g => g.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }
}
