namespace CloudSwitch.Contracts.Events;

/// <summary>A persisted job transition for API projections and authorized notifications.</summary>
public sealed record JobStatusChanged
{
    public required Guid EventId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required Guid JobId { get; init; }
    public required Guid DeploymentId { get; init; }
    public required Guid UserId { get; init; }
    public required JobStatus PreviousStatus { get; init; }
    public required JobStatus Status { get; init; }
    /// <summary>Number of execution attempts started; zero before the first execution.</summary>
    public required int Attempt { get; init; }
    /// <summary>Strictly increasing per-job transition sequence, beginning at one.</summary>
    public required long Sequence { get; init; }
    /// <summary>Optional stable failure code, safe to expose to the job owner.</summary>
    public string? ErrorCode { get; init; }
    /// <summary>Optional sanitized summary. Never include raw Terraform output or secrets.</summary>
    public string? ErrorMessage { get; init; }
}
