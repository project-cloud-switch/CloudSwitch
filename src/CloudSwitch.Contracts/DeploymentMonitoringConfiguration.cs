namespace CloudSwitch.Contracts;

/// <summary>Registered monitoring input. Configuration distribution belongs to the services.</summary>
public sealed record DeploymentMonitoringConfiguration
{
    public required Guid DeploymentId { get; init; }
    public required Guid UserId { get; init; }
    public required string Provider { get; init; }
    public required Uri Endpoint { get; init; }
    public required HealthCheckPolicy HealthCheck { get; init; }
    public RecoveryPolicy Recovery { get; init; } = new();
}
