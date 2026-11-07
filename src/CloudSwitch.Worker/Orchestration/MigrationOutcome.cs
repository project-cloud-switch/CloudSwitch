using CloudSwitch.Contracts;

namespace CloudSwitch.Worker.Orchestration;

public sealed record MigrationOutcome
{
    public required bool IsSuccess { get; init; }
    public required JobStatus FinalStatus { get; init; }
    public required MigrationPhase FinalPhase { get; init; }
    public string? TargetEndpoint { get; init; }
    public string? PublicEntrypoint { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public long LastSequence { get; init; }

    public static MigrationOutcome Succeeded(string targetEndpoint, string publicEntrypoint, long sequence) => new()
    {
        IsSuccess = true,
        FinalStatus = JobStatus.Succeeded,
        FinalPhase = MigrationPhase.Completed,
        TargetEndpoint = targetEndpoint,
        PublicEntrypoint = publicEntrypoint,
        LastSequence = sequence
    };

    public static MigrationOutcome Failed(
        MigrationPhase phase,
        string errorCode,
        string errorMessage,
        long sequence,
        string? targetEndpoint = null) => new()
    {
        IsSuccess = false,
        FinalStatus = JobStatus.Failed,
        FinalPhase = phase,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage,
        LastSequence = sequence,
        TargetEndpoint = targetEndpoint
    };
}
