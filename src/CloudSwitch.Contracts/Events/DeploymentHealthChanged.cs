namespace CloudSwitch.Contracts.Events;

/// <summary>A persisted availability transition, independent of migration jobs.</summary>
public sealed record DeploymentHealthChanged
{
    public required Guid EventId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required Guid DeploymentId { get; init; }
    public required Guid UserId { get; init; }
    public required string Provider { get; init; }
    public required DeploymentHealthStatus PreviousStatus { get; init; }
    public required DeploymentHealthStatus Status { get; init; }

    /// <summary>Active outage episode, or the episode resolved by this transition; null if none.</summary>
    public required Guid? IncidentId { get; init; }

    /// <summary>Strictly increasing per deployment across provider changes, beginning at one.</summary>
    public required long Sequence { get; init; }
    public required int ConsecutiveFailures { get; init; }

    /// <summary>Sanitized failure category; never raw response bodies or credentials.</summary>
    public string? ReasonCode { get; init; }
}
