using CloudSwitch.Contracts;
using CloudSwitch.Worker.Infrastructure;
using Xunit;

namespace CloudSwitch.Worker.Tests;

public class InfrastructureExecutorTests
{
    private readonly DeterministicInfrastructureExecutor _executor = new();

    private static ProvisioningRequest CreateValidRequest(Guid? deploymentId = null) => new()
    {
        JobId = Guid.NewGuid(),
        DeploymentId = deploymentId ?? Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        TargetConfigurationId = Guid.NewGuid(),
        Provider = "selectel",
        TerraformModule = "container-vm-selectel:v1.0.0",
        Timeout = TimeSpan.FromMinutes(5),
        Specification = new DeploymentSpecification
        {
            Name = "demo-app",
            ContainerImage = "cr.yandex/myorg/demo:v1.2.3@sha256:4f53cda18c21a48c",
            Environment = "production",
            Provider = "selectel",
            ContainerPort = 8080,
            CpuCores = 1.0,
            MemoryMb = 1024,
            SecretReferences = new Dictionary<string, string>
            {
                ["DB_PASSWORD"] = "vault://secret/prod/db#password"
            }
        }
    };

    [Fact]
    public async Task Provision_succeeds_with_valid_pinned_image_and_approved_module()
    {
        var request = CreateValidRequest();

        var result = await _executor.ProvisionAsync(request);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.TargetEndpoint);
        Assert.StartsWith("http://10.240.0.", result.TargetEndpoint);
        Assert.EndsWith(":8080", result.TargetEndpoint);
        Assert.NotNull(result.DeployedIdentity);
        Assert.StartsWith("selectel-vm-", result.DeployedIdentity);
        Assert.NotNull(result.StateFingerprint);
    }

    [Fact]
    public async Task Repeated_execution_converges_on_same_target_state()
    {
        var deploymentId = Guid.NewGuid();
        var request1 = CreateValidRequest(deploymentId);
        var request2 = CreateValidRequest(deploymentId);

        var result1 = await _executor.ProvisionAsync(request1);
        var result2 = await _executor.ProvisionAsync(request2);

        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);
        Assert.Equal(result1.TargetEndpoint, result2.TargetEndpoint);
        Assert.Equal(result1.DeployedIdentity, result2.DeployedIdentity);
        Assert.Equal(result1.StateFingerprint, result2.StateFingerprint);
    }

    [Fact]
    public async Task Reconcile_discovers_existing_target_state()
    {
        var deploymentId = Guid.NewGuid();
        var request = CreateValidRequest(deploymentId);

        var preReconcile = await _executor.ReconcileAsync(new ReconciliationRequest
        {
            DeploymentId = deploymentId,
            Provider = "selectel",
            TerraformModule = "container-vm-selectel:v1.0.0"
        });
        Assert.False(preReconcile.Exists);

        var provResult = await _executor.ProvisionAsync(request);
        Assert.True(provResult.IsSuccess);

        var postReconcile = await _executor.ReconcileAsync(new ReconciliationRequest
        {
            DeploymentId = deploymentId,
            Provider = "selectel",
            TerraformModule = "container-vm-selectel:v1.0.0"
        });

        Assert.True(postReconcile.Exists);
        Assert.True(postReconcile.IsHealthy);
        Assert.Equal(provResult.TargetEndpoint, postReconcile.ObservedTargetEndpoint);
        Assert.Equal(provResult.DeployedIdentity, postReconcile.DeployedIdentity);
    }

    [Fact]
    public async Task Rejects_unapproved_terraform_module()
    {
        var request = CreateValidRequest() with { TerraformModule = "unapproved-random-module" };

        var result = await _executor.ProvisionAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_MODULE", result.ErrorCode);
    }

    [Fact]
    public async Task Rejects_unapproved_provider()
    {
        var request = CreateValidRequest() with { Provider = "unsupported-cloud" };

        var result = await _executor.ProvisionAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_PROVIDER", result.ErrorCode);
    }

    [Fact]
    public async Task Rejects_unpinned_latest_image()
    {
        var request = CreateValidRequest();
        var spec = request.Specification with { ContainerImage = "docker.io/myorg/demo:latest" };
        var unpinnedRequest = request with { Specification = spec };

        var result = await _executor.ProvisionAsync(unpinnedRequest);

        Assert.False(result.IsSuccess);
        Assert.Equal("IMAGE_NOT_PINNED", result.ErrorCode);
    }

    [Fact]
    public async Task Rejects_untagged_image()
    {
        var request = CreateValidRequest();
        var spec = request.Specification with { ContainerImage = "docker.io/myorg/demo" };
        var untaggedRequest = request with { Specification = spec };

        var result = await _executor.ProvisionAsync(untaggedRequest);

        Assert.False(result.IsSuccess);
        Assert.Equal("IMAGE_NOT_PINNED", result.ErrorCode);
    }

    [Fact]
    public async Task Timeout_is_enforced()
    {
        var request = CreateValidRequest() with { Timeout = TimeSpan.Zero };

        var result = await _executor.ProvisionAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("TIMEOUT", result.ErrorCode);
    }

    [Fact]
    public void Output_sanitizer_masks_credentials_and_tokens()
    {
        const string rawLog = "Error connecting with password='SecretPassword123' and Bearer eyJhbGciOiJIUzI1Ni. Token: abcdef123456";
        var sanitized = OutputSanitizer.Sanitize(rawLog, explicitSecrets: new[] { "SecretPassword123" });

        Assert.DoesNotContain("SecretPassword123", sanitized);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1Ni", sanitized);
        Assert.Contains(OutputSanitizer.RedactedMask, sanitized);
    }

    [Theory]
    [InlineData("DATABASE_PASSWORD=Secret123!", "DATABASE_PASSWORD=***REDACTED***")]
    [InlineData("{\"apiKey\": \"sk-ant-12345\"}", "{\"apiKey\": \"***REDACTED***\"}")]
    [InlineData("Authorization: Bearer my-jwt-token-value", "Authorization: Bearer ***REDACTED***")]
    [InlineData("access_key: \"AKIAIOSFODNN7EXAMPLE\"", "access_key: \"***REDACTED***\"")]
    public void Output_sanitizer_redacts_common_secret_formats(string input, string expected)
    {
        var result = OutputSanitizer.Sanitize(input);
        Assert.Equal(expected, result);
    }
}
