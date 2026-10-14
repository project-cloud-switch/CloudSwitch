using System.Text.Json;
using CloudSwitch.Contracts.Events;
using Xunit;

namespace CloudSwitch.Contracts.Tests;

public class HealthContractTests
{
    [Fact]
    public void Confirmed_failure_preserves_incident_identity_and_ordering()
    {
        var health = SerializationTests.Read<DeploymentHealthChanged>("deployment-health-changed.v1.json");

        Assert.Equal(DeploymentHealthStatus.Healthy, health.PreviousStatus);
        Assert.Equal(DeploymentHealthStatus.Unhealthy, health.Status);
        Assert.Equal(Guid.Parse("99999999-9999-9999-9999-999999999999"), health.IncidentId);
        Assert.Equal(2, health.Sequence);
        Assert.Equal(3, health.ConsecutiveFailures);
        Assert.Null(health.ReasonCode);
        Assert.Equal(health, JsonSerializer.Deserialize<DeploymentHealthChanged>(
            JsonSerializer.Serialize(health, SerializationTests.JsonOptions), SerializationTests.JsonOptions));
    }

    [Fact]
    public void Recovery_event_can_close_the_same_incident()
    {
        var failure = SerializationTests.Read<DeploymentHealthChanged>("deployment-health-changed.v1.json");
        var recovered = failure with
        {
            EventId = Guid.NewGuid(), PreviousStatus = DeploymentHealthStatus.Unhealthy,
            Status = DeploymentHealthStatus.Healthy, Sequence = failure.Sequence + 1, ConsecutiveFailures = 0
        };

        var restored = JsonSerializer.Deserialize<DeploymentHealthChanged>(
            JsonSerializer.Serialize(recovered, SerializationTests.JsonOptions), SerializationTests.JsonOptions);
        Assert.Equal(recovered, restored);
        Assert.Equal(failure.IncidentId, restored!.IncidentId);
    }

    [Fact]
    public void Missing_recovery_configuration_defaults_to_notification_only()
    {
        const string json = """
            {
              "deploymentId": "44444444-4444-4444-4444-444444444444",
              "userId": "55555555-5555-5555-5555-555555555555",
              "provider": "source-cloud",
              "endpoint": "https://app.example.org",
              "healthCheck": {
                "path": "/health", "intervalSeconds": 30, "timeoutSeconds": 5,
                "failureThreshold": 3, "recoveryThreshold": 2
              }
            }
            """;
        var config = JsonSerializer.Deserialize<DeploymentMonitoringConfiguration>(json, SerializationTests.JsonOptions)!;

        Assert.Equal(RecoveryMode.NotifyOnly, config.Recovery.Mode);
        Assert.Null(config.Recovery.TargetConfigurationId);
        Assert.Equal(3, config.HealthCheck.FailureThreshold);
        Assert.Equal(2, config.HealthCheck.RecoveryThreshold);

        var automatic = config with
        {
            Recovery = new RecoveryPolicy { Mode = RecoveryMode.Automatic, TargetConfigurationId = Guid.NewGuid() }
        };
        Assert.Equal(automatic, JsonSerializer.Deserialize<DeploymentMonitoringConfiguration>(
            JsonSerializer.Serialize(automatic, SerializationTests.JsonOptions), SerializationTests.JsonOptions));
    }

    [Theory]
    [InlineData(DeploymentHealthStatus.Unknown, "Unknown")]
    [InlineData(DeploymentHealthStatus.Healthy, "Healthy")]
    [InlineData(DeploymentHealthStatus.Unhealthy, "Unhealthy")]
    public void Health_status_wire_names_are_stable(DeploymentHealthStatus status, string name) =>
        Assert.Equal($"\"{name}\"", JsonSerializer.Serialize(status));

    [Theory]
    [InlineData(RecoveryMode.NotifyOnly, "NotifyOnly")]
    [InlineData(RecoveryMode.Automatic, "Automatic")]
    public void Recovery_mode_wire_names_are_stable(RecoveryMode mode, string name) =>
        Assert.Equal($"\"{name}\"", JsonSerializer.Serialize(mode));
}
