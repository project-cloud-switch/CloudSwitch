using System.ComponentModel.DataAnnotations;

namespace CloudSwitch.Worker;

public sealed class WorkerOptions
{
    public bool Simulation { get; init; }
    [Range(1, 20)] public int MaxAttempts { get; init; } = 3;
    [Range(3, 3600)] public int LeaseSeconds { get; init; } = 120;
    [Range(1, 3600)] public int RecoveryIntervalSeconds { get; init; } = 30;
    [Range(1, 3600)] public int PhaseTimeoutSeconds { get; init; } = 300;
    [Range(1, 7200)] public int OverallTimeoutSeconds { get; init; } = 900;
    [Range(1, 100)] public int MaxConcurrentRecoveries { get; init; } = 4;
    [Range(5, 3600)] public int PendingGracePeriodSeconds { get; init; } = 60;
}
