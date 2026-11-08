using CloudSwitch.Api.Authentication;
using CloudSwitch.Api.Contracts;
using CloudSwitch.Api.Infrastructure;
using CloudSwitch.Persistence;
using CloudSwitch.Persistence.Domain;
using CloudSwitch.Persistence.Repositories;

namespace CloudSwitch.Api.Services;

public sealed class TargetService(
    ICurrentAccount accounts,
    DeploymentRepository deployments,
    TargetRepository targets,
    JobRepository jobs,
    UnitOfWork unitOfWork,
    ApprovedModules modules)
{
    public IReadOnlyList<ApprovedModule> ListModules()
        => modules.All;

    public async Task<TargetConfiguration> CreateAsync(
        Guid deploymentId, TargetRequest request, CancellationToken cancellationToken)
    {
        RequestValidation.ThrowIfInvalid(RequestValidation.Target(request, modules));
        var account = await accounts.GetAsync(cancellationToken);
        await using var transaction = await unitOfWork.BeginDeploymentTransactionAsync(deploymentId, cancellationToken);
        await ResourceAccess.DeploymentAsync(deployments, deploymentId, account.Id, cancellationToken);
        await ResourceAccess.RequireIdleAsync(jobs, deploymentId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var target = TargetConfiguration.Create(deploymentId, Definition(request), now);
        targets.Add(target);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return target;
    }

    public async Task<List<TargetConfiguration>> ListAsync(
        Guid deploymentId, int offset, int limit, CancellationToken cancellationToken)
    {
        ResourceAccess.Page(offset, limit);
        var account = await accounts.GetAsync(cancellationToken);
        await ResourceAccess.DeploymentAsync(deployments, deploymentId, account.Id, cancellationToken);
        return await targets.ListAsync(deploymentId, offset, limit, cancellationToken);
    }

    public async Task<TargetConfiguration> GetAsync(Guid deploymentId, Guid id, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(cancellationToken);
        await ResourceAccess.DeploymentAsync(deployments, deploymentId, account.Id, cancellationToken);
        return await FindAsync(deploymentId, id, cancellationToken);
    }

    public async Task<TargetConfiguration> UpdateAsync(
        Guid deploymentId, Guid id, TargetRequest request, long? expectedRevision, CancellationToken cancellationToken)
    {
        RequestValidation.ThrowIfInvalid(RequestValidation.Target(request, modules));
        var account = await accounts.GetAsync(cancellationToken);
        await using var transaction = await unitOfWork.BeginDeploymentTransactionAsync(deploymentId, cancellationToken);
        await ResourceAccess.DeploymentAsync(deployments, deploymentId, account.Id, cancellationToken);
        var target = await FindAsync(deploymentId, id, cancellationToken);
        ResourceAccess.MatchRevision(expectedRevision, target.Revision);
        await ResourceAccess.RequireIdleAsync(jobs, deploymentId, cancellationToken);
        target.UpdateConfiguration(Definition(request), DateTimeOffset.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return target;
    }

    public async Task DeleteAsync(Guid deploymentId, Guid id, long? expectedRevision, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(cancellationToken);
        await using var transaction = await unitOfWork.BeginDeploymentTransactionAsync(deploymentId, cancellationToken);
        var deployment = await ResourceAccess.DeploymentAsync(deployments, deploymentId, account.Id, cancellationToken);
        var target = await FindAsync(deploymentId, id, cancellationToken);
        ResourceAccess.MatchRevision(expectedRevision, target.Revision);
        await ResourceAccess.RequireIdleAsync(jobs, deploymentId, cancellationToken);
        if (deployment.Definition.Recovery.TargetConfigurationId == id)
            throw new ApiRequestException(409, "target_in_use", "Remove this target from the recovery policy before deleting it.");
        target.Delete(DateTimeOffset.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<TargetConfiguration> FindAsync(Guid deploymentId, Guid id, CancellationToken cancellationToken)
        => await targets.FindAsync(deploymentId, id, cancellationToken)
            ?? throw new ApiRequestException(404, "target_not_found", "Target configuration was not found.");

    private TargetDefinition Definition(TargetRequest request)
    {
        var module = modules.Find(request.ModuleId, request.Provider)
            ?? throw new ApiRequestException(409, "target_not_approved", "The selected module is no longer approved.");
        return new TargetDefinition(request.Provider, module.Id, module.Source, module.Revision,
            request.Inputs, request.SecretReferences);
    }
}
