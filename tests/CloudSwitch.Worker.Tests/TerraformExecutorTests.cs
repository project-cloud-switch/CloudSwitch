using System.Net;
using System.Text.Json;
using CloudSwitch.Contracts;
using CloudSwitch.Worker.Infrastructure;
using Xunit;

namespace CloudSwitch.Worker.Tests;

public sealed class TerraformExecutorTests
{
    [Fact]
    public async Task Deterministic_vault_resolver_resolves_references_predictably()
    {
        var resolver = new DeterministicVaultResolver();
        var secret = await resolver.ResolveSecretAsync("vault://secret/data/prod/db#password");
        Assert.NotNull(secret);
        Assert.StartsWith("resolved-secret-for-", secret);
        Assert.Contains("prod-db-password", secret);

        var dict = new Dictionary<string, string>
        {
            ["DB"] = "vault://secret/prod/db#pass",
            ["API"] = "vault://secret/prod/api#key"
        };
        var resolvedDict = await resolver.ResolveSecretsAsync(dict);
        Assert.Equal(2, resolvedDict.Count);
        Assert.Contains("pass", resolvedDict["DB"]);
        Assert.Contains("key", resolvedDict["API"]);
    }

    [Fact]
    public async Task Scoped_vault_resolver_handles_kv2_structure()
    {
        using var handler = new MockHttpMessageHandler(async (request, ct) =>
        {
            Assert.Equal("/v1/secret/data/app", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-token", request.Headers.GetValues("X-Vault-Token").Single());

            var responseJson = JsonSerializer.Serialize(new
            {
                data = new
                {
                    data = new
                    {
                        password = "vault-super-secret-value"
                    }
                }
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://vault.example.test:8200") };
        using var resolver = new ScopedVaultSecretResolver(new VaultOptions
        {
            Address = new Uri("http://vault.example.test:8200"),
            Token = "test-token"
        }, httpClient);

        var secret = await resolver.ResolveSecretAsync("vault://secret/data/app#password");
        Assert.Equal("vault-super-secret-value", secret);
    }

    [Fact]
    public async Task Scoped_vault_resolver_throws_on_missing_key()
    {
        using var handler = new MockHttpMessageHandler(async (request, ct) =>
        {
            var responseJson = JsonSerializer.Serialize(new
            {
                data = new { data = new { keyA = "valA" } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://vault.example.test:8200") };
        using var resolver = new ScopedVaultSecretResolver(new VaultOptions
        {
            Address = new Uri("http://vault.example.test:8200"),
            Token = "test-token"
        }, httpClient);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            resolver.ResolveSecretAsync("vault://secret/data/app#nonExistentKey"));
    }

    [Fact]
    public async Task Terraform_executor_rejects_unapproved_provider()
    {
        var runner = new MockTerraformProcessRunner();
        var vault = new DeterministicVaultResolver();
        var executor = new TerraformInfrastructureExecutor(runner, vault);

        var spec = CreateSampleSpecification() with { Provider = "unapproved-cloud" };
        var request = CreateSampleProvisioningRequest(spec, provider: "unapproved-cloud");

        var result = await executor.ProvisionAsync(request);
        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_PROVIDER", result.ErrorCode);
    }

    [Fact]
    public async Task Terraform_executor_rejects_unapproved_module()
    {
        var runner = new MockTerraformProcessRunner();
        var vault = new DeterministicVaultResolver();
        var executor = new TerraformInfrastructureExecutor(runner, vault);

        var spec = CreateSampleSpecification() with { TerraformModule = "unapproved-module:v1.0.0" };
        var request = CreateSampleProvisioningRequest(spec, module: "unapproved-module:v1.0.0");

        var result = await executor.ProvisionAsync(request);
        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_MODULE", result.ErrorCode);
    }

    [Fact]
    public async Task Terraform_executor_rejects_unpinned_image()
    {
        var runner = new MockTerraformProcessRunner();
        var vault = new DeterministicVaultResolver();
        var executor = new TerraformInfrastructureExecutor(runner, vault);

        var spec = CreateSampleSpecification() with { ContainerImage = "docker.io/myorg/app:latest" };
        var request = CreateSampleProvisioningRequest(spec);

        var result = await executor.ProvisionAsync(request);
        Assert.False(result.IsSuccess);
        Assert.Equal("IMAGE_NOT_PINNED", result.ErrorCode);
    }

    [Fact]
    public async Task Terraform_executor_provisions_and_captures_outputs()
    {
        var runner = new MockTerraformProcessRunner(outputsJson: JsonSerializer.Serialize(new
        {
            target_endpoint = new { value = "http://185.12.34.56:8080" },
            deployed_identity = new { value = "selectel-vm-app" }
        }));
        var vault = new DeterministicVaultResolver();
        var tempDir = Path.Combine(Path.GetTempPath(), "tf_test_" + Guid.NewGuid().ToString("N"));

        try
        {
            var executor = new TerraformInfrastructureExecutor(runner, vault, baseWorkingDirectory: tempDir);
            var spec = CreateSampleSpecification();
            var request = CreateSampleProvisioningRequest(spec);

            var result = await executor.ProvisionAsync(request);

            Assert.True(result.IsSuccess);
            Assert.Equal("http://185.12.34.56:8080", result.TargetEndpoint);
            Assert.Equal("selectel-vm-app", result.DeployedIdentity);
            Assert.NotNull(result.StateFingerprint);
            Assert.Contains("init -input=false", runner.CommandsExecuted[0]);
            Assert.Contains("apply -input=false", runner.CommandsExecuted[1]);

            // Now test reconciliation of the same state (should be Found and healthy)
            var reconcileResult = await executor.ReconcileAsync(new ReconciliationRequest
            {
                DeploymentId = request.DeploymentId,
                Provider = request.Provider,
                TerraformModule = request.TerraformModule,
                ExpectedTargetEndpoint = "http://185.12.34.56:8080",
                StateFingerprint = result.StateFingerprint
            });

            Assert.True(reconcileResult.Exists);
            Assert.Equal("http://185.12.34.56:8080", reconcileResult.ObservedTargetEndpoint);
            Assert.True(reconcileResult.IsHealthy);

            // Reconcile with mismatched endpoint should report Conflict
            var conflictResult = await executor.ReconcileAsync(new ReconciliationRequest
            {
                DeploymentId = request.DeploymentId,
                Provider = request.Provider,
                TerraformModule = request.TerraformModule,
                ExpectedTargetEndpoint = "http://10.99.99.99:8080",
                StateFingerprint = result.StateFingerprint
            });

            Assert.Equal("CONFLICTING_TARGET_STATE", conflictResult.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    private static DeploymentSpecification CreateSampleSpecification() => new()
    {
        Name = "test-service",
        ContainerImage = "docker.io/myorg/app:v1.2.3",
        Environment = "production",
        Provider = "selectel",
        ContainerPort = 8080,
        CpuCores = 1.0,
        MemoryMb = 512,
        TargetConfigurationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        TerraformModule = "container-vm-selectel:v1.0.0",
        HealthCheck = new HealthCheckPolicy { Path = "/healthz", IntervalSeconds = 2, TimeoutSeconds = 2, FailureThreshold = 3, RecoveryThreshold = 2 },
        EntrypointDomain = "app.example.com",
        TrafficRoutingProvider = "cloudflare",
        Env = new Dictionary<string, string> { ["ENV_A"] = "valA" },
        SecretReferences = new Dictionary<string, string> { ["API_KEY"] = "vault://secret/prod#key" }
    };

    private static ProvisioningRequest CreateSampleProvisioningRequest(
        DeploymentSpecification spec,
        string provider = "selectel",
        string module = "container-vm-selectel:v1.0.0") => new()
    {
        JobId = Guid.NewGuid(),
        DeploymentId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Specification = spec,
        TargetConfigurationId = spec.TargetConfigurationId!.Value,
        Provider = provider,
        TerraformModule = module
    };

    private sealed class MockTerraformProcessRunner(string? outputsJson = null) : ITerraformProcessRunner
    {
        public List<string> CommandsExecuted { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            string executable,
            string arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            CommandsExecuted.Add($"{arguments}");
            var stdout = "";
            if (arguments.Contains("output -json"))
            {
                stdout = outputsJson ?? JsonSerializer.Serialize(new
                {
                    target_endpoint = new { value = "http://10.240.0.1:8080" },
                    deployed_identity = new { value = "mock-vm-1" }
                });
            }

            return Task.FromResult(new ProcessExecutionResult(
                ExitCode: 0,
                StandardOutput: stdout,
                StandardError: "",
                Duration: TimeSpan.FromMilliseconds(50),
                TimedOut: false,
                Cancelled: false));
        }
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
