namespace CloudSwitch.Contracts.Events;

/// <summary>A durable request to execute a specific job using its submission-time snapshot.</summary>
public sealed record DeploymentRequested
{
    /// <summary>Stable logical event identifier, preserved when the outbox republishes.</summary>
    public required Guid EventId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required Guid JobId { get; init; }
    public required Guid DeploymentId { get; init; }
    public required Guid UserId { get; init; }
    public required DeploymentSpecification Specification { get; init; }

    /// <summary>Identifies whether this job was requested manually or automatically by Doctor.</summary>
    public DeploymentTrigger Trigger { get; init; } = DeploymentTrigger.Manual;

    /// <summary>Incident identifier when triggered automatically by Doctor.</summary>
    public Guid? IncidentId { get; init; }
}

