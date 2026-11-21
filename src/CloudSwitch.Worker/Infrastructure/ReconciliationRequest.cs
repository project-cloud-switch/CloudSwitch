using CloudSwitch.Contracts;

namespace CloudSwitch.Worker.Infrastructure;

/// <summary>
/// Parameters for reconciling target Terraform state before resuming an interrupted job.
/// </summary>
public sealed record ReconciliationRequest
{
    public required Guid DeploymentId { get; init; }
    public required string Provider { get; init; }
    public required string TerraformModule { get; init; }
    public string? ExpectedTargetEndpoint { get; init; }
    public string? StateFingerprint { get; init; }
    public DeploymentSpecification? Specification { get; init; }
    public Guid? TargetConfigurationId { get; init; }
    public long FencingToken { get; init; } = 0;
    public string? ExpectedApplicationIdentity { get; init; }
    public string? WorkingDirectory { get; init; }
}
