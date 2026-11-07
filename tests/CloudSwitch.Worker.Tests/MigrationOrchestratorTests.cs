using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Orchestration;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudSwitch.Worker.Tests;

public class MigrationOrchestratorTests
{
    private static DeploymentRequested CreateValidDeploymentRequest() => new()
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
            Name = "payments",
            ContainerImage = "docker.io/myorg/pay:1.0.0@sha256:abc12345",
            Environment = "production",
            Provider = "selectel",
            ContainerPort = 8080,
            TargetConfigurationId = Guid.NewGuid(),
            TerraformModule = "container-vm-selectel:v1.0.0",
            EntrypointDomain = "pay.example.com",
            HealthCheck = new HealthCheckPolicy
            {
                Path = "/healthz",
                IntervalSeconds = 2,
                TimeoutSeconds = 5,
                FailureThreshold = 3,
                RecoveryThreshold = 2
            }
        }
    };

    [Fact]
    public async Task Happy_path_completes_all_four_phases_and_increments_sequence()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var executor = new DeterministicInfrastructureExecutor();
        var healthValidator = new DeterministicHealthValidator(shouldSucceed: true);
        var routingService = new DeterministicTrafficRoutingService(shouldCutoverSucceed: true, shouldVerifySucceed: true);

        var orchestrator = new MigrationOrchestrator(executor, healthValidator, routingService, harness.Bus);
        var request = CreateValidDeploymentRequest();

        var outcome = await orchestrator.ExecuteMigrationAsync(request);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(JobStatus.Succeeded, outcome.FinalStatus);
        Assert.Equal(MigrationPhase.Completed, outcome.FinalPhase);
        Assert.NotNull(outcome.TargetEndpoint);
        Assert.Equal("https://pay.example.com", outcome.PublicEntrypoint);
        Assert.Equal(5, outcome.LastSequence);

        var publishedEvents = harness.Published.Select<JobStatusChanged>().ToList();
        Assert.Equal(5, publishedEvents.Count);

        var phases = publishedEvents.Select(x => x.Context.Message.Phase).ToList();
        Assert.Equal(new[]
        {
            MigrationPhase.Provisioning,
            MigrationPhase.TargetValidation,
            MigrationPhase.TrafficCutover,
            MigrationPhase.EntrypointVerification,
            MigrationPhase.Completed
        }, phases);

        // Verify strictly increasing sequence 1..5
        var sequences = publishedEvents.Select(x => x.Context.Message.Sequence).ToList();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, sequences);
    }

    [Fact]
    public async Task Target_health_failure_prevents_traffic_cutover()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var executor = new DeterministicInfrastructureExecutor();
        // Target fails health validation:
        var healthValidator = new DeterministicHealthValidator(shouldSucceed: false);
        var routingService = new DeterministicTrafficRoutingService();

        var orchestrator = new MigrationOrchestrator(executor, healthValidator, routingService, harness.Bus);
        var request = CreateValidDeploymentRequest();

        var outcome = await orchestrator.ExecuteMigrationAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(JobStatus.Failed, outcome.FinalStatus);
        Assert.Equal(MigrationPhase.TargetValidation, outcome.FinalPhase);
        Assert.Equal("TARGET_HEALTH_CHECK_FAILED", outcome.ErrorCode);
        Assert.Contains("cutover was prevented", outcome.ErrorMessage);

        var publishedEvents = harness.Published.Select<JobStatusChanged>().ToList();
        Assert.Equal(3, publishedEvents.Count);

        // TrafficCutover and EntrypointVerification MUST NOT have been reached
        Assert.DoesNotContain(publishedEvents, e => e.Context.Message.Phase == MigrationPhase.TrafficCutover);
        Assert.DoesNotContain(publishedEvents, e => e.Context.Message.Phase == MigrationPhase.EntrypointVerification);
        Assert.DoesNotContain(publishedEvents, e => e.Context.Message.Phase == MigrationPhase.Completed);
    }

    [Fact]
    public async Task Provisioning_failure_halts_immediately()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var executor = new DeterministicInfrastructureExecutor();
        var healthValidator = new DeterministicHealthValidator();
        var routingService = new DeterministicTrafficRoutingService();

        var orchestrator = new MigrationOrchestrator(executor, healthValidator, routingService, harness.Bus);
        var request = CreateValidDeploymentRequest();
        // Invalid module forces provisioning failure
        var spec = request.Specification with { TerraformModule = "unapproved-module" };
        var badRequest = request with { Specification = spec };

        var outcome = await orchestrator.ExecuteMigrationAsync(badRequest);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(JobStatus.Failed, outcome.FinalStatus);
        Assert.Equal(MigrationPhase.Provisioning, outcome.FinalPhase);
        Assert.Equal("INVALID_MODULE", outcome.ErrorCode);

        var publishedEvents = harness.Published.Select<JobStatusChanged>().ToList();
        Assert.Equal(2, publishedEvents.Count);
        Assert.Equal(JobStatus.Failed, publishedEvents.Last().Context.Message.Status);
    }
}
