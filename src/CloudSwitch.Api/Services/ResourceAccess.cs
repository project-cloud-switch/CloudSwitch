using CloudSwitch.Persistence;
using CloudSwitch.Persistence.Domain;
using CloudSwitch.Persistence.Repositories;
using CloudSwitch.Api.Infrastructure;

namespace CloudSwitch.Api.Services;

internal static class ResourceAccess
{
    public static async Task<Deployment> DeploymentAsync(DeploymentRepository deployments, Guid id, Guid userId, CancellationToken ct)
        => await deployments.FindOwnedAsync(id, userId, ct)
            ?? throw new ApiRequestException(404, "deployment_not_found", "Deployment was not found.");

    public static async Task RequireIdleAsync(JobRepository jobs, Guid id, CancellationToken ct)
    {
        if (await jobs.HasActiveAsync(id, ct))
            throw new ApiRequestException(409, "active_job_exists", "Configuration cannot change while a migration is active.");
    }

    public static void MatchRevision(long? expectedRevision, long revision)
    {
        if (expectedRevision is null)
            throw new ApiRequestException(428, "revision_required", "Supply the resource ETag in the If-Match header.");
        if (expectedRevision != revision)
            throw new ApiRequestException(412, "revision_changed", "The resource changed. Reload it before retrying.");
    }

    public static void Page(int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 100)
            throw new ApiRequestException(400, "invalid_pagination", "Offset must be nonnegative; limit must be between 1 and 100.");
    }

    public static async Task ValidateRecoveryAsync(TargetRepository targets, Guid deploymentId, DeploymentDefinition definition, ApprovedModules modules, CancellationToken ct)
    {
        if (definition.Recovery.TargetConfigurationId is not { } targetId)
            return;
        var target = await targets.FindAsync(deploymentId, targetId, ct);
        if (target is null || modules.Find(target.Configuration.ModuleId, target.Configuration.Provider) is not { } approved
            || approved.Source != target.Configuration.ModuleSource || approved.Revision != target.Configuration.ModuleRevision
            || target.Configuration.Inputs.Keys.Except(approved.AllowedInputs).Any())
            throw new ApiRequestException(400, "invalid_recovery_target", "Recovery must reference an approved target belonging to this deployment.");
    }
}
