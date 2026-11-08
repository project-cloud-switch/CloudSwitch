using CloudSwitch.Contracts;

namespace CloudSwitch.Api.Contracts;

public sealed record DeploymentRequest
{
    public required string Name { get; init; }
    public required DeploymentDefinitionDto Definition { get; init; }
}

public sealed record DeploymentDefinitionDto
{
    public required string Environment { get; init; }
    public required string SourceProvider { get; init; }
    public required Uri SourceEndpoint { get; init; }
    public required ApplicationConfiguration Application { get; init; }
    public required HealthCheckPolicy HealthCheck { get; init; }
    public required TrafficConfiguration Traffic { get; init; }
    public RecoveryPolicy Recovery { get; init; } = new();
    public bool MonitoringEnabled { get; init; } = true;
}

public sealed record TargetRequest
{
    public required string Provider { get; init; }
    public required string ModuleId { get; init; }
    public Dictionary<string, string> Inputs { get; init; } = [];
    public Dictionary<string, string> SecretReferences { get; init; } = [];
}

public sealed record JobRequest(Guid TargetConfigurationId);
