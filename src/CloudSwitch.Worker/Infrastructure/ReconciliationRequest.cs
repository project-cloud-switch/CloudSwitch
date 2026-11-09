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
    public string? WorkingDirectory { get; init; }
}
