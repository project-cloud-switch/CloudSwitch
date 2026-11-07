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

/// <summary>
/// Mutates traffic routing (DNS or Load Balancer) to direct traffic to the verified target container.
/// </summary>
public interface ITrafficRoutingService
{
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

    public DeterministicTrafficRoutingService(bool shouldCutoverSucceed = true, bool shouldVerifySucceed = true)
    {
        _shouldCutoverSucceed = shouldCutoverSucceed;
        _shouldVerifySucceed = shouldVerifySucceed;
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
