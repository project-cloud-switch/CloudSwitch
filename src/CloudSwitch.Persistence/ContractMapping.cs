using Contracts = CloudSwitch.Contracts;
using Events = CloudSwitch.Contracts.Events;
using Domain = CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Persistence;

public static class ContractMapping
{
    public static T ConvertEnum<T>(Enum value) where T : struct, Enum
        => Enum.Parse<T>(value.ToString());

    public static Domain.ApplicationConfiguration ToDomain(this Contracts.ApplicationConfiguration value)
        => new(value.Image, value.Port, value.CpuMillicores, value.MemoryMegabytes, value.Settings, value.SecretReferences);

    public static Contracts.ApplicationConfiguration ToContract(this Domain.ApplicationConfiguration value)
        => new()
        {
            Image = value.Image, Port = value.Port, CpuMillicores = value.CpuMillicores,
            MemoryMegabytes = value.MemoryMegabytes, Settings = new(value.Settings), SecretReferences = new(value.SecretReferences)
        };

    public static Domain.HealthCheckPolicy ToDomain(this Contracts.HealthCheckPolicy value)
        => new(value.Path, value.IntervalSeconds, value.TimeoutSeconds, value.FailureThreshold, value.RecoveryThreshold);

    public static Contracts.HealthCheckPolicy ToContract(this Domain.HealthCheckPolicy value)
        => new()
        {
            Path = value.Path, IntervalSeconds = value.IntervalSeconds, TimeoutSeconds = value.TimeoutSeconds,
            FailureThreshold = value.FailureThreshold, RecoveryThreshold = value.RecoveryThreshold
        };

    public static Domain.RecoveryPolicy ToDomain(this Contracts.RecoveryPolicy value)
        => new(ConvertEnum<Domain.RecoveryMode>(value.Mode), value.TargetConfigurationId);

    public static Contracts.RecoveryPolicy ToContract(this Domain.RecoveryPolicy value)
        => new() { Mode = ConvertEnum<Contracts.RecoveryMode>(value.Mode), TargetConfigurationId = value.TargetConfigurationId };

    public static Domain.TrafficConfiguration ToDomain(this Contracts.TrafficConfiguration value)
        => new(value.Driver, value.RecordName, value.ZoneReference, value.CredentialReference);

    public static Contracts.TrafficConfiguration ToContract(this Domain.TrafficConfiguration value)
        => new() { Driver = value.Driver, RecordName = value.RecordName, ZoneReference = value.ZoneReference, CredentialReference = value.CredentialReference };

    public static Contracts.TargetConfigurationSnapshot ToContract(this Domain.TargetConfigurationSnapshot value)
        => new()
        {
            Id = value.Id, Revision = value.Revision, Provider = value.Provider, ModuleId = value.ModuleId,
            ModuleSource = value.ModuleSource, ModuleRevision = value.ModuleRevision,
            Inputs = new(value.Inputs), SecretReferences = new(value.SecretReferences)
        };

    public static Contracts.DeploymentSpecification ToContract(this Domain.DeploymentSpecification value)
        => new()
        {
            Name = value.Name, ContainerImage = value.ContainerImage, Environment = value.Environment,
            Provider = value.Provider, Migration = value.Migration?.ToContract()
        };

    public static Contracts.MigrationSpecification ToContract(this Domain.MigrationSpecification value)
        => new()
        {
            ConfigurationRevision = value.ConfigurationRevision,
            Source = new Contracts.SourceDeploymentSnapshot
            {
                Provider = value.Source.Provider, Endpoint = value.Source.Endpoint, RuntimeRevision = value.Source.RuntimeRevision
            },
            Target = value.Target.ToContract(), Application = value.Application.ToContract(),
            TargetHealthCheck = value.TargetHealthCheck.ToContract(), Traffic = value.Traffic.ToContract()
        };

    public static Events.DeploymentRequested ToContract(this Domain.JobRequestSnapshot value)
        => new()
        {
            EventId = value.EventId, CorrelationId = value.CorrelationId, OccurredAt = value.OccurredAt,
            JobId = value.JobId, DeploymentId = value.DeploymentId, UserId = value.UserId,
            Specification = value.Specification.ToContract(), Trigger = ConvertEnum<Contracts.JobTrigger>(value.Trigger),
            IncidentId = value.IncidentId
        };

    public static Events.JobStatusChanged ToContract(this Domain.JobProgress value)
        => new()
        {
            EventId = value.EventId, CorrelationId = value.CorrelationId, OccurredAt = value.OccurredAt,
            JobId = value.JobId, DeploymentId = value.DeploymentId, UserId = value.UserId,
            PreviousStatus = ConvertEnum<Contracts.JobStatus>(value.PreviousStatus), Status = ConvertEnum<Contracts.JobStatus>(value.Status),
            Attempt = value.Attempt, Sequence = value.Sequence, ErrorCode = value.ErrorCode, ErrorMessage = value.ErrorMessage,
            Phase = value.Phase is { } phase ? ConvertEnum<Contracts.MigrationPhase>(phase) : null,
            TargetEndpoint = value.TargetEndpoint,
            Cutover = value.Cutover is { } cutover ? ConvertEnum<Contracts.CutoverOutcome>(cutover) : null
        };
}
