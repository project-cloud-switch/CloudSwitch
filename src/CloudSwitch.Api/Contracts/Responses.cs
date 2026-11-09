using CloudSwitch.Contracts;
using CloudSwitch.Persistence;
using Domain = CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Api.Contracts;

public sealed record DeploymentHealthResponse(Guid DeploymentId, string Provider, Uri Endpoint,
    DeploymentHealthStatus Status, long Sequence, Guid? IncidentId, DateTimeOffset? ObservedAt);

public sealed record DeploymentResponse(Guid Id, Guid UserId, string Name, long Revision,
    DeploymentDefinitionDto Definition, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static DeploymentResponse From(Domain.Deployment deployment)
        => new(deployment.Id, deployment.UserId, deployment.Name, deployment.Revision,
            deployment.Definition.ToDto(), deployment.IsDeleted, deployment.CreatedAt, deployment.UpdatedAt);
}

public sealed record TargetResponse(Guid Id, Guid DeploymentId, long Revision,
    TargetConfigurationSnapshot Configuration, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static TargetResponse From(Domain.TargetConfiguration target)
        => new(target.Id, target.DeploymentId, target.Revision, target.Configuration.ToContract(),
            target.IsDeleted, target.CreatedAt, target.UpdatedAt);
}

public sealed record JobResponse(Guid Id, Guid DeploymentId, Guid TargetConfigurationId, DeploymentTrigger Trigger,
    Guid? IncidentId, Guid CorrelationId, JobStatus Status, MigrationPhase Phase, int Attempt, long Sequence,
    Uri? TargetEndpoint, CutoverOutcome Cutover, string? ErrorCode, string? ErrorMessage,
    DeploymentSpecification Specification, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static JobResponse From(Domain.Job job) => new(job.Id, job.DeploymentId, job.TargetConfigurationId,
        ContractMapping.ConvertEnum<DeploymentTrigger>(job.Trigger), job.IncidentId, job.Request.CorrelationId,
        ContractMapping.ConvertEnum<JobStatus>(job.Status), job.Phase.ToContract(), job.Attempt, job.Sequence,
        job.TargetEndpoint, ContractMapping.ConvertEnum<CutoverOutcome>(job.Cutover), job.ErrorCode, job.ErrorMessage,
        job.Request.Specification.ToContract(), job.CreatedAt, job.UpdatedAt);
}
