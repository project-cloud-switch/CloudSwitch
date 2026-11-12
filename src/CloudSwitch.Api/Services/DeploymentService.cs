using CloudSwitch.Api.Authentication;
using CloudSwitch.Api.Contracts;
using CloudSwitch.Api.Infrastructure;
using CloudSwitch.Persistence;
using CloudSwitch.Persistence.Domain;
using CloudSwitch.Persistence.Repositories;
using Microsoft.Extensions.Options;

namespace CloudSwitch.Api.Services;

public sealed class DeploymentService(
    ICurrentAccount accounts,
    DeploymentRepository deployments,
    TargetRepository targets,
    JobRepository jobs,
    UnitOfWork unitOfWork,
    ApprovedModules modules,
    IOptions<EndpointOptions> options)
{
    public async Task<Deployment> CreateAsync(DeploymentRequest request, CancellationToken cancellationToken)
    {
        RequestValidation.ThrowIfInvalid(RequestValidation.Deployment(request, options.Value));
        var account = await accounts.GetAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var deployment = Deployment.Register(account.Id, request.Name, request.Definition.ToDomain(), now);
        await ResourceAccess.ValidateRecoveryAsync(targets, deployment.Id, deployment.Definition, modules, cancellationToken);
        deployments.Add(deployment);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return deployment;
    }

    public async Task<List<Deployment>> ListAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        ResourceAccess.Page(offset, limit);
        var account = await accounts.GetAsync(cancellationToken);
        return await deployments.ListOwnedAsync(account.Id, offset, limit, cancellationToken);
    }

    public async Task<Deployment> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(cancellationToken);
        return await ResourceAccess.DeploymentAsync(deployments, id, account.Id, cancellationToken);
    }

    public async Task<Deployment> UpdateAsync(
        Guid id, DeploymentRequest request, long? expectedRevision, CancellationToken cancellationToken)
    {
        RequestValidation.ThrowIfInvalid(RequestValidation.Deployment(request, options.Value));
        var account = await accounts.GetAsync(cancellationToken);
        await using var transaction = await unitOfWork.BeginDeploymentTransactionAsync(id, cancellationToken);
        var deployment = await ResourceAccess.DeploymentAsync(deployments, id, account.Id, cancellationToken);
        ResourceAccess.MatchRevision(expectedRevision, deployment.Revision);
        await ResourceAccess.RequireIdleAsync(jobs, id, cancellationToken);
        var definition = request.Definition.ToDomain();
        await ResourceAccess.ValidateRecoveryAsync(targets, id, definition, modules, cancellationToken);
        deployment.UpdateConfiguration(request.Name, definition, DateTimeOffset.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deployment;
    }

    public async Task DeleteAsync(Guid id, long? expectedRevision, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(cancellationToken);
        await using var transaction = await unitOfWork.BeginDeploymentTransactionAsync(id, cancellationToken);
        var deployment = await ResourceAccess.DeploymentAsync(deployments, id, account.Id, cancellationToken);
        ResourceAccess.MatchRevision(expectedRevision, deployment.Revision);
        await ResourceAccess.RequireIdleAsync(jobs, id, cancellationToken);
        deployment.Delete(DateTimeOffset.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<DeploymentHealthResponse> GetHealthAsync(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await GetAsync(id, cancellationToken);
        var health = await deployments.FindHealthAsync(id, cancellationToken);
        var runtime = await deployments.FindRuntimeAsync(id, cancellationToken);
        var currentObservation = health is { } observation && observation.ConfigurationRevision == deployment.Revision
            && observation.RuntimeRevision == (runtime?.Revision ?? 0) ? observation : null;
        return new DeploymentHealthResponse(
            id,
            runtime?.Provider ?? deployment.Definition.SourceProvider,
            runtime?.Endpoint ?? deployment.Definition.SourceEndpoint,
            ContractMapping.ConvertEnum<CloudSwitch.Contracts.DeploymentHealthStatus>(
                currentObservation?.Status ?? DeploymentHealthStatus.Unknown),
            health?.Sequence ?? 0,
            currentObservation?.IncidentId,
            currentObservation?.ObservedAt);
    }

    public async Task<Deployment> UpdateMonitoringAsync(
        Guid id, MonitoringUpdateRequest request, long? expectedRevision, CancellationToken cancellationToken)
    {
        RequestValidation.ThrowIfInvalid(RequestValidation.Monitoring(request));
        var account = await accounts.GetAsync(cancellationToken);
        await using var transaction = await unitOfWork.BeginDeploymentTransactionAsync(id, cancellationToken);
        var deployment = await ResourceAccess.DeploymentAsync(deployments, id, account.Id, cancellationToken);
        ResourceAccess.MatchRevision(expectedRevision, deployment.Revision);
        var healthCheck = request.HealthCheck.ToDomain();
        var recovery = request.Recovery.ToDomain();
        var candidate = deployment.Definition.WithMonitoring(healthCheck, recovery, request.MonitoringEnabled);
        await ResourceAccess.ValidateRecoveryAsync(targets, id, candidate, modules, cancellationToken);
        deployment.UpdateMonitoring(healthCheck, recovery, request.MonitoringEnabled, DateTimeOffset.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deployment;
    }
}
