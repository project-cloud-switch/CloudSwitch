using Microsoft.EntityFrameworkCore;
using CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Persistence.Repositories;

public sealed class DeploymentRepository(CloudSwitchDbContext db)
{
    public Task<Deployment?> FindOwnedAsync(
        Guid id, Guid userId, CancellationToken cancellationToken, bool includeDeleted = false)
        => db.Deployments.SingleOrDefaultAsync(
            deployment => deployment.Id == id && deployment.UserId == userId
                && (includeDeleted || !deployment.IsDeleted), cancellationToken);

    public Task<List<Deployment>> ListOwnedAsync(
        Guid userId, int offset, int limit, CancellationToken cancellationToken)
        => db.Deployments.AsNoTracking()
            .Where(deployment => deployment.UserId == userId && !deployment.IsDeleted)
            .OrderByDescending(deployment => deployment.CreatedAt)
            .ThenBy(deployment => deployment.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<DeploymentRuntime?> FindRuntimeAsync(Guid deploymentId, CancellationToken cancellationToken)
        => db.Runtimes.AsNoTracking().SingleOrDefaultAsync(
            runtime => runtime.DeploymentId == deploymentId, cancellationToken);

    public Task<HealthObservation?> FindHealthAsync(Guid deploymentId, CancellationToken cancellationToken)
        => db.Health.AsNoTracking().SingleOrDefaultAsync(
            observation => observation.DeploymentId == deploymentId, cancellationToken);

    public Task<Incident?> FindIncidentAsync(Guid deploymentId, Guid? incidentId, CancellationToken cancellationToken)
        => db.Incidents.AsNoTracking().SingleOrDefaultAsync(
            incident => incident.Id == incidentId && incident.DeploymentId == deploymentId, cancellationToken);

    public void Add(Deployment deployment)
        => db.Deployments.Add(deployment);
}
