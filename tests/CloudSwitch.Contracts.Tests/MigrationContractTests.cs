using System.Text.Json;
using CloudSwitch.Contracts.Events;
using Xunit;

namespace CloudSwitch.Contracts.Tests;

public class MigrationContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = SerializationTests.JsonOptions;

    [Theory]
    [InlineData(MigrationPhase.Pending, "Pending")]
    [InlineData(MigrationPhase.Provisioning, "Provisioning")]
    [InlineData(MigrationPhase.TargetValidation, "TargetValidation")]
    [InlineData(MigrationPhase.TrafficCutover, "TrafficCutover")]
    [InlineData(MigrationPhase.EntrypointVerification, "EntrypointVerification")]
    [InlineData(MigrationPhase.Completed, "Completed")]
    public void Migration_phase_wire_names_are_stable(MigrationPhase phase, string name)
    {
        Assert.Equal($"\"{name}\"", JsonSerializer.Serialize(phase, JsonOptions));
        Assert.Equal(phase, JsonSerializer.Deserialize<MigrationPhase>($"\"{name}\"", JsonOptions));
    }

    [Fact]
    public void Unknown_migration_phase_is_rejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MigrationPhase>("\"InvalidPhase\"", JsonOptions));
    }

    [Theory]
    [InlineData(DeploymentTrigger.Manual, "Manual")]
    [InlineData(DeploymentTrigger.Automatic, "Automatic")]
    public void Deployment_trigger_wire_names_are_stable(DeploymentTrigger trigger, string name)
    {
        Assert.Equal($"\"{name}\"", JsonSerializer.Serialize(trigger, JsonOptions));
        Assert.Equal(trigger, JsonSerializer.Deserialize<DeploymentTrigger>($"\"{name}\"", JsonOptions));
    }

    [Fact]
    public void Unknown_deployment_trigger_is_rejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DeploymentTrigger>("\"Scheduled\"", JsonOptions));
    }

    [Fact]
    public void Deployment_request_with_full_snapshot_serializes_and_roundtrips()
    {
        var targetConfigId = Guid.NewGuid();
        var request = new DeploymentRequested
        {
            EventId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            JobId = Guid.NewGuid(),
            DeploymentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Trigger = DeploymentTrigger.Automatic,
            IncidentId = Guid.NewGuid(),
            Specification = new DeploymentSpecification
            {
                Name = "payment-gateway",
                ContainerImage = "docker.io/myorg/payment:1.4.2@sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                Environment = "production",
                Provider = "selectel",
                ContainerPort = 8080,
                CpuCores = 2.0,
                MemoryMb = 2048,
                ImagePullSecretReference = "vault://secret/data/infra/registry#token",
                Env = new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Production",
                    ["LOG_LEVEL"] = "Information"
                },
                SecretReferences = new Dictionary<string, string>
                {
                    ["DB_CONNECTION"] = "vault://secret/data/prod/payment/db#conn",
                    ["API_KEY"] = "vault://secret/data/prod/payment/stripe#key"
                },
                TargetConfigurationId = targetConfigId,
                TerraformModule = "container-vm-selectel:v1.0.0",
                HealthCheck = new HealthCheckPolicy
                {
                    Path = "/healthz",
                    IntervalSeconds = 2,
                    TimeoutSeconds = 5,
                    FailureThreshold = 3,
                    RecoveryThreshold = 2
                },
                EntrypointDomain = "pay.company.com",
                TrafficRoutingProvider = "cloudflare"
            }
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<DeploymentRequested>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(request.EventId, deserialized.EventId);
        Assert.Equal(request.CorrelationId, deserialized.CorrelationId);
        Assert.Equal(request.JobId, deserialized.JobId);
        Assert.Equal(request.DeploymentId, deserialized.DeploymentId);
        Assert.Equal(request.UserId, deserialized.UserId);
        Assert.Equal(DeploymentTrigger.Automatic, deserialized.Trigger);
        Assert.Equal(request.IncidentId, deserialized.IncidentId);
        Assert.Equal(request.Specification, deserialized.Specification);
        Assert.Equal(8080, deserialized.Specification.ContainerPort);
        Assert.Equal(2.0, deserialized.Specification.CpuCores);
        Assert.Equal(2048, deserialized.Specification.MemoryMb);
        Assert.Equal("vault://secret/data/infra/registry#token", deserialized.Specification.ImagePullSecretReference);
        Assert.Equal("vault://secret/data/prod/payment/db#conn", deserialized.Specification.SecretReferences!["DB_CONNECTION"]);
        Assert.Equal("pay.company.com", deserialized.Specification.EntrypointDomain);
        Assert.Equal("container-vm-selectel:v1.0.0", deserialized.Specification.TerraformModule);
        Assert.Equal("/healthz", deserialized.Specification.HealthCheck!.Path);
    }

    [Fact]
    public void Job_status_event_with_phase_and_target_endpoint_roundtrips()
    {
        var status = new JobStatusChanged
        {
            EventId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            JobId = Guid.NewGuid(),
            DeploymentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PreviousStatus = JobStatus.Running,
            Status = JobStatus.Running,
            Attempt = 1,
            Sequence = 3,
            Phase = MigrationPhase.TargetValidation,
            TargetEndpoint = "http://185.12.34.56:8080"
        };

        var json = JsonSerializer.Serialize(status, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<JobStatusChanged>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(MigrationPhase.TargetValidation, deserialized.Phase);
        Assert.Equal("http://185.12.34.56:8080", deserialized.TargetEndpoint);
        Assert.Equal(1, deserialized.Attempt);
        Assert.Equal(3, deserialized.Sequence);
    }

    [Fact]
    public void Migration_failure_captures_phase_and_error_details()
    {
        var failure = new JobStatusChanged
        {
            EventId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            JobId = Guid.NewGuid(),
            DeploymentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PreviousStatus = JobStatus.Running,
            Status = JobStatus.Failed,
            Attempt = 1,
            Sequence = 4,
            Phase = MigrationPhase.TargetValidation,
            TargetEndpoint = "http://185.12.34.56:8080",
            ErrorCode = "TARGET_HEALTH_CHECK_FAILED",
            ErrorMessage = "Target container failed health check on /healthz after 3 attempts. Cutover prevented."
        };

        var json = JsonSerializer.Serialize(failure, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<JobStatusChanged>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(JobStatus.Failed, deserialized.Status);
        Assert.Equal(MigrationPhase.TargetValidation, deserialized.Phase);
        Assert.Equal("TARGET_HEALTH_CHECK_FAILED", deserialized.ErrorCode);
        Assert.Contains("Cutover prevented", deserialized.ErrorMessage);
    }
}
