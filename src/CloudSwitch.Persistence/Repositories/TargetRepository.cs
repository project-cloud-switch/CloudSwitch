using Microsoft.EntityFrameworkCore;
using CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Persistence.Repositories;

public sealed class TargetRepository(CloudSwitchDbContext db)
{
    public Task<TargetConfiguration?> FindAsync(
        Guid deploymentId, Guid id, CancellationToken cancellationToken)
        => db.Targets.SingleOrDefaultAsync(
            target => target.Id == id && target.DeploymentId == deploymentId && !target.IsDeleted,
            cancellationToken);

    public Task<List<TargetConfiguration>> ListAsync(
        Guid deploymentId, int offset, int limit, CancellationToken cancellationToken)
        => db.Targets.AsNoTracking()
            .Where(target => target.DeploymentId == deploymentId && !target.IsDeleted)
            .OrderBy(target => target.CreatedAt)
            .ThenBy(target => target.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public void Add(TargetConfiguration target)
        => db.Targets.Add(target);
}
