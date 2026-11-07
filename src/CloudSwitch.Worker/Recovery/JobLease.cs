using CloudSwitch.Contracts;

namespace CloudSwitch.Worker.Recovery;

/// <summary>
/// Represents a fenced lease and execution checkpoint for a migration job.
/// </summary>
public sealed record JobLease
{
    public required Guid JobId { get; init; }
    public required Guid DeploymentId { get; init; }
    public required Guid UserId { get; init; }
    public required string WorkerId { get; init; }
    public required int Attempt { get; init; }
    public required DateTimeOffset LeaseExpiresAt { get; init; }
    public required JobStatus Status { get; init; }
    public required MigrationPhase CurrentPhase { get; init; }
    public string? TargetEndpoint { get; init; }
    public required DeploymentSpecification Specification { get; init; }
    public int MaxAttempts { get; init; } = 3;
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public bool IsExpired(DateTimeOffset now) => now >= LeaseExpiresAt;
    public bool HasExhaustedRetries => Attempt >= MaxAttempts;
}
