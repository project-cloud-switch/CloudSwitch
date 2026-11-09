using Contracts = CloudSwitch.Contracts;
using Events = CloudSwitch.Contracts.Events;
using Domain = CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Persistence;

public static class ContractMapping
{
    public static T ConvertEnum<T>(Enum value) where T : struct, Enum
        => Enum.Parse<T>(value.ToString());

    // Keep reading the existing persisted TargetHealth name while exposing the agreed wire phase.
    public static Contracts.MigrationPhase ToContract(this Domain.MigrationPhase phase)
        => phase == Domain.MigrationPhase.TargetHealth ? Contracts.MigrationPhase.TargetValidation : ConvertEnum<Contracts.MigrationPhase>(phase);

    public static Domain.MigrationPhase ToDomain(this Contracts.MigrationPhase phase)
        => phase == Contracts.MigrationPhase.TargetValidation ? Domain.MigrationPhase.TargetHealth : ConvertEnum<Domain.MigrationPhase>(phase);

    public static Domain.ApplicationConfiguration ToDomain(this Contracts.ApplicationConfiguration value)
        => new(value.Image, value.Port, value.CpuMillicores, value.MemoryMegabytes, value.Settings, value.SecretReferences, value.ImagePullSecretReference);

    public static Contracts.ApplicationConfiguration ToContract(this Domain.ApplicationConfiguration value)
        => new()
        {
            Image = value.Image, Port = value.Port, CpuMillicores = value.CpuMillicores,
            MemoryMegabytes = value.MemoryMegabytes, ImagePullSecretReference = value.ImagePullSecretReference, Settings = new(value.Settings), SecretReferences = new(value.SecretReferences)
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
            Provider = value.Provider,
            ContainerPort = value.Migration?.Application.Port ?? 80,
            CpuCores = (value.Migration?.Application.CpuMillicores ?? 1000) / 1000d,
            MemoryMb = value.Migration?.Application.MemoryMegabytes ?? 512,
            ImagePullSecretReference = value.Migration?.Application.ImagePullSecretReference,
            Env = value.Migration?.Application.Settings, SecretReferences = value.Migration?.Application.SecretReferences,
            TargetConfigurationId = value.Migration?.Target.Id,
            TerraformModule = value.Migration is { } migration ? $"{migration.Target.ModuleId}:{migration.Target.ModuleRevision}" : null,
            HealthCheck = value.Migration?.TargetHealthCheck.ToContract(),
            EntrypointDomain = value.Migration?.Traffic.RecordName, TrafficRoutingProvider = value.Migration?.Traffic.Driver
        };

    public static Events.DeploymentRequested ToContract(this Domain.JobRequestSnapshot value)
        => new()
        {
            EventId = value.EventId, CorrelationId = value.CorrelationId, OccurredAt = value.OccurredAt,
            JobId = value.JobId, DeploymentId = value.DeploymentId, UserId = value.UserId,
            Specification = value.Specification.ToContract(), Trigger = ConvertEnum<Contracts.DeploymentTrigger>(value.Trigger),
            IncidentId = value.IncidentId
        };

    public static Events.JobStatusChanged ToContract(this Domain.JobProgress value)
        => new()
        {
            EventId = value.EventId, CorrelationId = value.CorrelationId, OccurredAt = value.OccurredAt,
            JobId = value.JobId, DeploymentId = value.DeploymentId, UserId = value.UserId,
            PreviousStatus = ConvertEnum<Contracts.JobStatus>(value.PreviousStatus), Status = ConvertEnum<Contracts.JobStatus>(value.Status),
            Attempt = value.Attempt, Sequence = value.Sequence, ErrorCode = value.ErrorCode, ErrorMessage = value.ErrorMessage,
            Phase = value.Phase?.ToContract() ?? Contracts.MigrationPhase.Pending,
            TargetEndpoint = value.TargetEndpoint?.ToString(),
            Cutover = value.Cutover is { } cutover ? ConvertEnum<Contracts.CutoverOutcome>(cutover) : null
        };
}
