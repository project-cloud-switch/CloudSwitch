using CloudSwitch.Contracts;

namespace CloudSwitch.Worker.Infrastructure;

/// <summary>
/// Parameters required to provision or converge target infrastructure for a migration job.
/// </summary>
public sealed record ProvisioningRequest
{
    public required Guid JobId { get; init; }
    public required Guid DeploymentId { get; init; }
    public required Guid UserId { get; init; }
    public required DeploymentSpecification Specification { get; init; }
    public required Guid TargetConfigurationId { get; init; }
    public required string Provider { get; init; }
    public required string TerraformModule { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public int Attempt { get; init; } = 1;
    public string? WorkingDirectory { get; init; }
}
