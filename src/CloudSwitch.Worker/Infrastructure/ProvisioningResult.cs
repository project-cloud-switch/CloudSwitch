namespace CloudSwitch.Worker.Infrastructure;

/// <summary>
/// Output resulting from a target infrastructure provisioning or convergence attempt.
/// </summary>
public sealed record ProvisioningResult
{
    public required bool IsSuccess { get; init; }
    public string? TargetEndpoint { get; init; }
    public string? DeployedIdentity { get; init; }
    public string? StateFingerprint { get; init; }
    public string? ErrorCode { get; init; }
    public string? SanitizedOutput { get; init; }
    public TimeSpan Duration { get; init; }

    public static ProvisioningResult Success(
        string targetEndpoint,
        string deployedIdentity,
        string stateFingerprint,
        TimeSpan duration,
        string? sanitizedOutput = null) => new()
    {
        IsSuccess = true,
        TargetEndpoint = targetEndpoint,
        DeployedIdentity = deployedIdentity,
        StateFingerprint = stateFingerprint,
        Duration = duration,
        SanitizedOutput = sanitizedOutput
    };

    public static ProvisioningResult Failure(
        string errorCode,
        string sanitizedOutput,
        TimeSpan duration) => new()
    {
        IsSuccess = false,
        ErrorCode = errorCode,
        SanitizedOutput = sanitizedOutput,
        Duration = duration
    };
}
