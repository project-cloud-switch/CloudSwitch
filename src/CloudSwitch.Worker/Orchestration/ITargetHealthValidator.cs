using CloudSwitch.Contracts;

namespace CloudSwitch.Worker.Orchestration;

public sealed record HealthValidationResult
{
    public required bool IsHealthy { get; init; }
    public int StatusCode { get; init; }
    public TimeSpan Latency { get; init; }
    public int SuccessfulProbes { get; init; }
    public int FailedProbes { get; init; }
    public string? ErrorMessage { get; init; }

    public static HealthValidationResult Healthy(int statusCode = 200, TimeSpan latency = default) => new()
    {
        IsHealthy = true,
        StatusCode = statusCode,
        Latency = latency == default ? TimeSpan.FromMilliseconds(45) : latency,
        SuccessfulProbes = 2,
        FailedProbes = 0
    };

    public static HealthValidationResult Unhealthy(string errorMessage, int statusCode = 0) => new()
    {
        IsHealthy = false,
        StatusCode = statusCode,
        Latency = TimeSpan.FromSeconds(5),
        SuccessfulProbes = 0,
        FailedProbes = 3,
        ErrorMessage = errorMessage
    };
}

/// <summary>
/// Probes target container endpoint before cutover to ensure it responds healthy.
/// </summary>
public interface ITargetHealthValidator
{
    Task<HealthValidationResult> ValidateAsync(
        string targetEndpoint,
        HealthCheckPolicy? policy,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Deterministic health validator for CI tests.
/// </summary>
public sealed class DeterministicHealthValidator : ITargetHealthValidator
{
    private readonly bool _shouldSucceed;

    public DeterministicHealthValidator(bool shouldSucceed = true)
    {
        _shouldSucceed = shouldSucceed;
    }

    public Task<HealthValidationResult> ValidateAsync(
        string targetEndpoint,
        HealthCheckPolicy? policy,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(HealthValidationResult.Unhealthy("Health check cancelled."));
        }

        if (!_shouldSucceed)
        {
            return Task.FromResult(HealthValidationResult.Unhealthy(
                $"Target endpoint {targetEndpoint} failed health check: connection refused or returned 503.",
                statusCode: 503));
        }

        return Task.FromResult(HealthValidationResult.Healthy());
    }
}
