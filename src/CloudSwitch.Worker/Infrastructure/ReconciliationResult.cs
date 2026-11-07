namespace CloudSwitch.Worker.Infrastructure;

/// <summary>
/// Status of observed target infrastructure discovered during reconciliation.
/// </summary>
public sealed record ReconciliationResult
{
    public required bool Exists { get; init; }
    public bool IsHealthy { get; init; }
    public string? ObservedTargetEndpoint { get; init; }
    public string? DeployedIdentity { get; init; }
    public string? StateFingerprint { get; init; }
    public string? ErrorCode { get; init; }
    public string? SanitizedMessage { get; init; }

    public static ReconciliationResult Found(
        string observedTargetEndpoint,
        string deployedIdentity,
        string stateFingerprint,
        bool isHealthy = true) => new()
    {
        Exists = true,
        IsHealthy = isHealthy,
        ObservedTargetEndpoint = observedTargetEndpoint,
        DeployedIdentity = deployedIdentity,
        StateFingerprint = stateFingerprint
    };

    public static ReconciliationResult NotFound(string? reason = null) => new()
    {
        Exists = false,
        IsHealthy = false,
        SanitizedMessage = reason ?? "Target infrastructure does not exist."
    };

    public static ReconciliationResult Conflict(string errorCode, string message) => new()
    {
        Exists = true,
        IsHealthy = false,
        ErrorCode = errorCode,
        SanitizedMessage = message
    };
}
