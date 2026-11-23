using CloudSwitch.Contracts;
using CloudSwitch.Worker.Orchestration;

namespace CloudSwitch.Worker.Probing;

public sealed class HttpTargetHealthValidator(IHttpHealthProber prober) : ITargetHealthValidator
{
    public async Task<HealthValidationResult> ValidateAsync(
        string targetEndpoint,
        HealthCheckPolicy? policy,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return HealthValidationResult.Unhealthy("Health check cancelled.");

        if (!Uri.TryCreate(targetEndpoint, UriKind.Absolute, out var baseUri))
            return HealthValidationResult.Unhealthy($"Invalid target endpoint URL: {targetEndpoint}");

        var path = policy?.Path ?? "/healthz";
        if (!path.StartsWith('/'))
            path = "/" + path;

        var probeUri = new Uri(baseUri, path);

        var probeTimeout = TimeSpan.FromSeconds(policy?.TimeoutSeconds is > 0 ? policy.TimeoutSeconds : 3);
        var interval = TimeSpan.FromSeconds(policy?.IntervalSeconds is > 0 ? policy.IntervalSeconds : 1);
        var requiredSuccesses = policy?.RecoveryThreshold is > 0 ? policy.RecoveryThreshold : 2;
        var requiredFailures = policy?.FailureThreshold is > 0 ? policy.FailureThreshold : 3;

        // Overall readiness deadline: bound total probe loop duration
        var maxOverallDuration = TimeSpan.FromSeconds(
            Math.Max(30, (policy?.TimeoutSeconds ?? 3) * (requiredFailures + requiredSuccesses) + 10));
        var deadline = DateTimeOffset.UtcNow.Add(maxOverallDuration);

        var consecutiveSuccesses = 0;
        var consecutiveFailures = 0;
        var lastStatusCode = 0;
        var lastLatency = TimeSpan.Zero;
        string? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
                return HealthValidationResult.Unhealthy("Health check cancelled.");

            var result = await prober.ProbeAsync(probeUri, probeTimeout, cancellationToken);
            lastStatusCode = result.StatusCode;
            lastLatency = result.Latency;

            if (result.IsHealthy)
            {
                consecutiveSuccesses++;
                consecutiveFailures = 0;
                if (consecutiveSuccesses >= requiredSuccesses)
                {
                    return new HealthValidationResult
                    {
                        IsHealthy = true,
                        StatusCode = result.StatusCode,
                        Latency = result.Latency,
                        SuccessfulProbes = consecutiveSuccesses,
                        FailedProbes = consecutiveFailures
                    };
                }
            }
            else
            {
                consecutiveFailures++;
                consecutiveSuccesses = 0;
                lastError = result.ErrorMessage;
                if (consecutiveFailures >= requiredFailures)
                {
                    return new HealthValidationResult
                    {
                        IsHealthy = false,
                        StatusCode = result.StatusCode,
                        Latency = result.Latency,
                        SuccessfulProbes = consecutiveSuccesses,
                        FailedProbes = consecutiveFailures,
                        ErrorMessage = $"Health check failed on {probeUri} after {consecutiveFailures} consecutive failures: {result.ErrorMessage}"
                    };
                }
            }

            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return HealthValidationResult.Unhealthy("Health check cancelled.");
            }
        }

        return HealthValidationResult.Unhealthy(
            $"Target health validation on {probeUri} timed out before reaching threshold. Last error: {lastError ?? "Timed out"}",
            statusCode: lastStatusCode);
    }
}
