using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace CloudSwitch.Worker.Infrastructure;

/// <summary>
/// Deterministic in-memory infrastructure executor for automated CI and integration tests.
/// Enforces module allowlists, validates input mapping, guarantees state convergence across retries,
/// and sanitizes all output without requiring live cloud credentials.
/// </summary>
public sealed partial class DeterministicInfrastructureExecutor : IInfrastructureExecutor
{
    private static readonly HashSet<string> ApprovedModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "container-vm-selectel:v1.0.0",
        "container-vm-selectel",
        "selectel-container-vm",
        "docker-sim",
        "local-sim",
        "local-stateless-v1:local-simulation-v1"
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

        // 3. Validate Pinned Image (must not be untagged or latest)
        var image = request.Specification.ContainerImage;
        if (string.IsNullOrWhiteSpace(image) || !PinnedImage().IsMatch(image) || image.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
        {
            var msg = OutputSanitizer.Sanitize("Container image must use an explicit tag or a complete SHA-256 digest.");
            return Task.FromResult(ProvisioningResult.Failure("IMAGE_NOT_PINNED", msg, TimeSpan.FromMilliseconds(50)));
        }

        var specification = request.Specification;
        if (specification.TargetConfigurationId != request.TargetConfigurationId
            || specification.Provider != request.Provider || specification.TerraformModule != request.TerraformModule)
            return Task.FromResult(ProvisioningResult.Failure("INVALID_SNAPSHOT", "Target inputs must match the immutable snapshot.", TimeSpan.Zero));
        var stateKey = $"{request.DeploymentId}:{request.Provider}";
        var ordered = specification with
        {
            Env = specification.Env?.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
            SecretReferences = specification.SecretReferences?.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value)
        };
        var identity = $"{request.DeploymentId:D}:{JsonSerializer.Serialize(ordered)}";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var ipByte = (request.DeploymentId.ToByteArray()[0] % 250) + 1;
        var desired = new DeployedState($"http://10.240.0.{ipByte}:{specification.ContainerPort}",
            $"{request.Provider.ToLowerInvariant()}-vm-{request.DeploymentId.ToString()[..8]}", fingerprint, DateTimeOffset.UtcNow);
        var state = _deployedStates.AddOrUpdate(stateKey, desired,
            (_, existing) => existing.StateFingerprint == fingerprint ? existing : desired);

        var duration = DateTimeOffset.UtcNow - startTime;
        var sanitizedLog = OutputSanitizer.Sanitize(
            $"Simulation apply complete. Target endpoint: {state.TargetEndpoint}");

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
            if (request.ExpectedTargetEndpoint is not null && request.ExpectedTargetEndpoint != state.TargetEndpoint
                || request.StateFingerprint is not null && request.StateFingerprint != state.StateFingerprint)
                return Task.FromResult(ReconciliationResult.Conflict("CONFLICTING_TARGET_STATE", "The target does not match the saved checkpoint."));
            return Task.FromResult(ReconciliationResult.Found(
                state.TargetEndpoint,
                state.DeployedIdentity,
                state.StateFingerprint,
                isHealthy: true));
        }

        return Task.FromResult(ReconciliationResult.NotFound(
            $"No existing target infrastructure found for deployment {request.DeploymentId} on provider {request.Provider}."));
    }
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9._:/-]*(?:@sha256:[a-fA-F0-9]{64}|:[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127})\z")]
    private static partial Regex PinnedImage();
}
