namespace CloudSwitch.Contracts;

/// <summary>Persisted per-deployment response to a confirmed unhealthy incident.</summary>
public sealed record RecoveryPolicy
{
    public RecoveryMode Mode { get; init; } = RecoveryMode.NotifyOnly;

    /// <summary>Approved fallback configuration reference; required in Automatic mode.</summary>
    public Guid? TargetConfigurationId { get; init; }
}
