namespace CloudSwitch.Worker.Orchestration;

public sealed record RoutingResult
{
    public required bool IsSuccess { get; init; }
    public string? EntrypointUrl { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }

    public static RoutingResult Success(string entrypointUrl) => new()
    {
        IsSuccess = true,
        EntrypointUrl = entrypointUrl,
        Message = "Traffic successfully switched and entrypoint verified."
    };

    public static RoutingResult Failure(string errorCode, string message) => new()
    {
        IsSuccess = false,
        ErrorCode = errorCode,
        Message = message
    };
}

public sealed record RoutingInspectionResult
{
    public bool Exists { get; init; }
    public string? CurrentTarget { get; init; }
    public bool MatchesExpectedTarget { get; init; }
    public bool IsConflict { get; init; }
    public string? ErrorMessage { get; init; }

    public static RoutingInspectionResult Matches(string target) => new()
    {
        Exists = true,
        CurrentTarget = target,
        MatchesExpectedTarget = true
    };

    public static RoutingInspectionResult PointsToSource(string source) => new()
    {
        Exists = true,
        CurrentTarget = source,
        MatchesExpectedTarget = false
    };

    public static RoutingInspectionResult Conflict(string currentTarget, string message) => new()
    {
        Exists = true,
        CurrentTarget = currentTarget,
        IsConflict = true,
        ErrorMessage = message
    };

    public static RoutingInspectionResult NotFound() => new()
    {
        Exists = false
    };
}

/// <summary>
/// Mutates traffic routing (DNS or Load Balancer) to direct traffic to the verified target container.
/// </summary>
public interface ITrafficRoutingService
{
    Task<RoutingInspectionResult> InspectAsync(
        string domain,
        string expectedTargetEndpoint,
        string? provider,
        CancellationToken cancellationToken = default)
        => Task.FromResult(RoutingInspectionResult.PointsToSource("http://source.example.com"));

    Task<RoutingResult> CutoverAsync(
        string domain,
        string targetEndpoint,
        string? provider,
        CancellationToken cancellationToken = default);

    Task<RoutingResult> VerifyEntrypointAsync(
        string domain,
        string healthPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Deterministic traffic routing service for CI tests.
/// </summary>
public sealed class DeterministicTrafficRoutingService : ITrafficRoutingService
{
    private readonly bool _shouldCutoverSucceed;
    private readonly bool _shouldVerifySucceed;
    private readonly string? _forcedCurrentTarget;
    private readonly bool? _forcedInspectMatches;
    private readonly bool _forcedConflict;
    private bool _hasSwitched;

    public DeterministicTrafficRoutingService(
        bool shouldCutoverSucceed = true,
        bool shouldVerifySucceed = true,
        string? forcedCurrentTarget = null,
        bool? forcedInspectMatches = null,
        bool forcedConflict = false)
    {
        _shouldCutoverSucceed = shouldCutoverSucceed;
        _shouldVerifySucceed = shouldVerifySucceed;
        _forcedCurrentTarget = forcedCurrentTarget;
        _forcedInspectMatches = forcedInspectMatches;
        _forcedConflict = forcedConflict;
    }

    public Task<RoutingInspectionResult> InspectAsync(
        string domain,
        string expectedTargetEndpoint,
        string? provider,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(RoutingInspectionResult.Conflict("", "Inspection cancelled."));

        if (_forcedConflict)
            return Task.FromResult(RoutingInspectionResult.Conflict("http://198.51.100.99:8080", "DNS points to unknown foreign endpoint."));

        if (_forcedInspectMatches.HasValue)
        {
            return Task.FromResult(_forcedInspectMatches.Value
                ? RoutingInspectionResult.Matches(expectedTargetEndpoint)
                : RoutingInspectionResult.PointsToSource(_forcedCurrentTarget ?? "http://10.240.0.1:8080"));
        }

        if (_hasSwitched)
            return Task.FromResult(RoutingInspectionResult.Matches(expectedTargetEndpoint));

        return Task.FromResult(RoutingInspectionResult.PointsToSource(_forcedCurrentTarget ?? "http://10.240.0.1:8080"));
    }

    public Task<RoutingResult> CutoverAsync(
        string domain,
        string targetEndpoint,
        string? provider,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(RoutingResult.Failure("CANCELLED", "Cutover operation cancelled."));

        if (!_shouldCutoverSucceed)
            return Task.FromResult(RoutingResult.Failure("ROUTING_API_FAILED", $"DNS provider failed to update record for {domain}."));

        _hasSwitched = true;
        return Task.FromResult(RoutingResult.Success($"https://{domain}"));
    }

    public Task<RoutingResult> VerifyEntrypointAsync(
        string domain,
        string healthPath,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(RoutingResult.Failure("CANCELLED", "Entrypoint verification cancelled."));

        if (!_shouldVerifySucceed)
            return Task.FromResult(RoutingResult.Failure("ENTRYPOINT_UNREACHABLE", $"Public entrypoint https://{domain}{healthPath} returned 502."));

        return Task.FromResult(RoutingResult.Success($"https://{domain}{healthPath}"));
    }
}
