using CloudSwitch.Persistence.Domain;
using MassTransit;
using CloudSwitch.Persistence.Repositories;

namespace CloudSwitch.Persistence;

public enum RejectionKind { NotFound, Invalid, Conflict }
public sealed class OperationRejectedException(RejectionKind kind, string code, string message) : Exception(message)
{
    public RejectionKind Kind { get; } = kind;
    public string Code { get; } = code;
}

public sealed record SubmitJob(Guid DeploymentId, Guid UserId, Guid TargetConfigurationId,
    JobTrigger Trigger, string? IdempotencyKey, Guid? IncidentId, Guid CorrelationId, long? HealthSequence = null);
public sealed record SubmissionResult(Job Job, bool Created);

/// <summary>Shared by API and Doctor. All creation and outbox writes use this DbContext transaction.</summary>
public sealed class JobSubmissionService(
    DeploymentRepository deployments,
    TargetRepository targets,
    JobRepository jobs,
    UnitOfWork unitOfWork,
    IPublishEndpoint publisher,
    ApprovedModules modules)
{
    public async Task<SubmissionResult> SubmitAsync(SubmitJob command, CancellationToken ct)
    {
        if (command.DeploymentId == Guid.Empty || command.UserId == Guid.Empty || command.TargetConfigurationId == Guid.Empty
            || command.CorrelationId == Guid.Empty || !Enum.IsDefined(command.Trigger))
            throw Reject(RejectionKind.Invalid, "invalid_job", "Job identities and trigger must be valid.");
        if (command.Trigger == JobTrigger.Manual && (string.IsNullOrWhiteSpace(command.IdempotencyKey)
            || command.IdempotencyKey.Length > 128 || command.IncidentId is not null))
            throw Reject(RejectionKind.Invalid, "invalid_idempotency_key", "Manual jobs require an Idempotency-Key of at most 128 characters and no incident.");
        if (command.Trigger == JobTrigger.Automatic && (command.IncidentId is null || command.IncidentId == Guid.Empty
            || command.IdempotencyKey is not null || command.HealthSequence is null or <= 0))
            throw Reject(RejectionKind.Invalid, "invalid_incident", "Automatic jobs require an incident and observed health sequence.");

        await using var transaction = await unitOfWork.BeginDeploymentTransactionAsync(command.DeploymentId, ct);
        var deployment = await deployments.FindOwnedAsync(command.DeploymentId, command.UserId, ct, includeDeleted: true)
            ?? throw Reject(RejectionKind.NotFound, "deployment_not_found", "Deployment was not found.");

        var previous = await jobs.FindPreviousSubmissionAsync(
            command.DeploymentId, command.Trigger, command.IdempotencyKey, command.IncidentId, ct);
        if (previous is not null)
        {
            if (previous.TargetConfigurationId != command.TargetConfigurationId)
                throw Reject(RejectionKind.Conflict, "idempotency_conflict", "This submission key was already used with a different target.");
            return new SubmissionResult(previous, false);
        }

        if (deployment.IsDeleted)
            throw Reject(RejectionKind.NotFound, "deployment_not_found", "Deployment was not found.");
        if (await jobs.HasActiveAsync(deployment.Id, ct))
            throw Reject(RejectionKind.Conflict, "active_job_exists", "A migration is already active for this deployment.");
        var target = await targets.FindAsync(deployment.Id, command.TargetConfigurationId, ct)
            ?? throw Reject(RejectionKind.NotFound, "target_not_found", "Target configuration was not found.");
        var approved = modules.Find(target.Configuration.ModuleId, target.Configuration.Provider);
        if (approved is null || approved.Source != target.Configuration.ModuleSource || approved.Revision != target.Configuration.ModuleRevision
            || target.Configuration.Inputs.Keys.Except(approved.AllowedInputs).Any())
            throw Reject(RejectionKind.Conflict, "target_not_approved", "The target module is no longer approved. Update the target before submitting.");

        var credentials = deployment.Definition.Application.SecretReferences.Values
            .Concat(target.Configuration.SecretReferences.Values)
            .Concat(new[] { deployment.Definition.Application.ImagePullSecretReference, deployment.Definition.Traffic.CredentialReference });
        if (credentials.Any(reference => reference is not null && !reference.StartsWith("vault://", StringComparison.Ordinal)))
            throw Reject(RejectionKind.Conflict, "vault_reference_required", "Update legacy credential references to vault://path#key before submitting a new job.");

        var runtime = await deployments.FindRuntimeAsync(deployment.Id, ct);
        var source = new SourceDeploymentSnapshot(
            runtime?.Provider ?? deployment.Definition.SourceProvider,
            runtime?.Endpoint ?? deployment.Definition.SourceEndpoint,
            runtime?.Revision ?? 0);
        if (source.Provider == target.Configuration.Provider)
            throw Reject(RejectionKind.Conflict, "target_is_active", "Choose a target provider different from the currently active provider.");

        if (command.Trigger == JobTrigger.Automatic)
        {
            var health = await deployments.FindHealthAsync(deployment.Id, ct);
            var incident = await deployments.FindIncidentAsync(deployment.Id, command.IncidentId, ct);
            if (!deployment.Definition.MonitoringEnabled || deployment.Definition.Recovery.Mode != RecoveryMode.Automatic
                || deployment.Definition.Recovery.TargetConfigurationId != target.Id || incident is null || incident.ResolvedAt is not null
                || health is null || health.Status != DeploymentHealthStatus.Unhealthy || health.IncidentId != command.IncidentId
                || health.Sequence != command.HealthSequence || health.ConfigurationRevision != deployment.ObservationRevision
                || health.RuntimeRevision != source.RuntimeRevision)
                throw Reject(RejectionKind.Conflict, "stale_recovery", "The recovery policy or current health no longer permits this automatic job.");
        }

        var now = DateTimeOffset.UtcNow;
        var specification = new DeploymentSpecification(
            deployment.Name, deployment.Definition.Application.Image, deployment.Definition.Environment,
            target.Configuration.Provider, new MigrationSpecification(deployment.Revision, source, target.Configuration,
                deployment.Definition.Application, deployment.Definition.HealthCheck, deployment.Definition.Traffic));
        var job = Job.Create(deployment.Id, deployment.UserId, specification, command.Trigger,
            command.IdempotencyKey, command.IncidentId, command.CorrelationId, now);
        var request = job.Request.ToContract();
        jobs.Add(job);
        await publisher.Publish(request, publish =>
        {
            publish.MessageId = request.EventId;
            publish.CorrelationId = request.CorrelationId;
        }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new SubmissionResult(job, true);
    }

    private static OperationRejectedException Reject(RejectionKind kind, string code, string message) => new(kind, code, message);
}
