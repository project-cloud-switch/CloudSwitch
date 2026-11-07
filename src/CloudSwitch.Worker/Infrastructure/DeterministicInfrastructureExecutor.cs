using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CloudSwitch.Worker.Infrastructure;

/// <summary>
/// Deterministic in-memory infrastructure executor for automated CI and integration tests.
/// Enforces module allowlists, validates input mapping, guarantees state convergence across retries,
/// and sanitizes all output without requiring live cloud credentials.
/// </summary>
public sealed class DeterministicInfrastructureExecutor : IInfrastructureExecutor
{
    private static readonly HashSet<string> ApprovedModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "container-vm-selectel:v1.0.0",
        "container-vm-selectel",
        "selectel-container-vm",
        "docker-sim",
        "local-sim"
    };

    private static readonly HashSet<string> ApprovedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "selectel",
        "local",
        "docker",
        "yandex-fallback"
    };

    // Tracks simulated deployed state per (DeploymentId, Provider) to ensure convergence across retries
    private readonly ConcurrentDictionary<string, DeployedState> _deployedStates = new();

    private sealed record DeployedState(
        string TargetEndpoint,
        string DeployedIdentity,
        string StateFingerprint,
        DateTimeOffset ProvisionedAt);

    public Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(ProvisioningResult.Failure(
                "OPERATION_CANCELLED",
                "Provisioning was cancelled before execution.",
                TimeSpan.Zero));
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            return Task.FromResult(ProvisioningResult.Failure(
                "TIMEOUT",
                OutputSanitizer.Sanitize("Execution timed out after 0 seconds."),
                TimeSpan.Zero));
        }

        // 1. Validate Provider
        if (!ApprovedProviders.Contains(request.Provider))
        {
            var msg = OutputSanitizer.Sanitize($"Provider '{request.Provider}' is not supported or approved.");
            return Task.FromResult(ProvisioningResult.Failure("INVALID_PROVIDER", msg, TimeSpan.FromMilliseconds(50)));
        }

        // 2. Validate Terraform Module
        if (string.IsNullOrWhiteSpace(request.TerraformModule) || !ApprovedModules.Contains(request.TerraformModule))
        {
            var msg = OutputSanitizer.Sanitize($"Module '{request.TerraformModule}' is not in the approved Terraform module allowlist.");
            return Task.FromResult(ProvisioningResult.Failure("INVALID_MODULE", msg, TimeSpan.FromMilliseconds(50)));
        }

        // 3. Validate Pinned Image (must not be latest)
        var image = request.Specification.ContainerImage;
        if (string.IsNullOrWhiteSpace(image) || image.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
        {
            var msg = OutputSanitizer.Sanitize("Container image must be pinned with explicit tag or sha256 digest, 'latest' is prohibited.");
            return Task.FromResult(ProvisioningResult.Failure("IMAGE_NOT_PINNED", msg, TimeSpan.FromMilliseconds(50)));
        }

        // 4. Converge or create state
        var stateKey = $"{request.DeploymentId}:{request.Provider}";
        var state = _deployedStates.GetOrAdd(stateKey, _ =>
        {
            var port = request.Specification.ContainerPort > 0 ? request.Specification.ContainerPort : 80;
            var ipByte = (request.DeploymentId.ToByteArray()[0] % 250) + 1;
            var targetEndpoint = $"http://10.240.0.{ipByte}:{port}";
            var deployedIdentity = $"{request.Provider.ToLowerInvariant()}-vm-{request.DeploymentId.ToString()[..8]}";

            using var sha = SHA256.Create();
            var rawBytes = Encoding.UTF8.GetBytes($"{request.DeploymentId}:{request.Provider}:{request.TerraformModule}:{request.TargetConfigurationId}");
            var fingerprint = Convert.ToHexString(sha.ComputeHash(rawBytes)).ToLowerInvariant();

            return new DeployedState(targetEndpoint, deployedIdentity, fingerprint, DateTimeOffset.UtcNow);
        });

        var duration = DateTimeOffset.UtcNow - startTime;
        var sanitizedLog = OutputSanitizer.Sanitize(
            $"Terraform apply complete. Resources: 1 added, 0 changed, 0 destroyed. Target endpoint: {state.TargetEndpoint}");

        return Task.FromResult(ProvisioningResult.Success(
            state.TargetEndpoint,
            state.DeployedIdentity,
            state.StateFingerprint,
            duration,
            sanitizedLog));
    }

    public Task<ReconciliationResult> ReconcileAsync(ReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        var stateKey = $"{request.DeploymentId}:{request.Provider}";

        if (_deployedStates.TryGetValue(stateKey, out var state))
        {
            return Task.FromResult(ReconciliationResult.Found(
                state.TargetEndpoint,
                state.DeployedIdentity,
                state.StateFingerprint,
                isHealthy: true));
        }

        return Task.FromResult(ReconciliationResult.NotFound(
            $"No existing target infrastructure found for deployment {request.DeploymentId} on provider {request.Provider}."));
    }
}
