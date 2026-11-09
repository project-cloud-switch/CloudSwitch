using System.ComponentModel.DataAnnotations;

namespace CloudSwitch.Worker;

public sealed class WorkerOptions
{
    public bool Simulation { get; init; }
    [Range(1, 20)] public int MaxAttempts { get; init; } = 3;
    [Range(3, 3600)] public int LeaseSeconds { get; init; } = 120;
    [Range(1, 3600)] public int RecoveryIntervalSeconds { get; init; } = 30;
}
