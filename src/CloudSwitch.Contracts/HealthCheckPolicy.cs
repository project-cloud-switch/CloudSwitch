namespace CloudSwitch.Contracts;

/// <summary>HTTP probing thresholds. Services validate ranges and allowed paths.</summary>
public sealed record HealthCheckPolicy
{
    public required string Path { get; init; }
    public required int IntervalSeconds { get; init; }
    public required int TimeoutSeconds { get; init; }
    public required int FailureThreshold { get; init; }
    public required int RecoveryThreshold { get; init; }
}
