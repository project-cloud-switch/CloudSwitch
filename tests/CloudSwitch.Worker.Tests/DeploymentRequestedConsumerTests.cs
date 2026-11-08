using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Worker.Consumers;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Orchestration;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudSwitch.Worker.Tests;

public class DeploymentRequestedConsumerTests
{
    [Fact]
    public async Task Consumer_processes_deployment_request_and_publishes_status_events()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IInfrastructureExecutor, DeterministicInfrastructureExecutor>()
            .AddSingleton<ITargetHealthValidator, DeterministicHealthValidator>()
            .AddSingleton<ITrafficRoutingService, DeterministicTrafficRoutingService>()
            .AddScoped<IMigrationOrchestrator, MigrationOrchestrator>()
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.SetTestTimeouts(testTimeout: TimeSpan.FromSeconds(10), testInactivityTimeout: TimeSpan.FromSeconds(3));
                cfg.AddConsumer<DeploymentRequestedConsumer>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var request = new DeploymentRequested
        {
            EventId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            JobId = Guid.NewGuid(),
            DeploymentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Trigger = DeploymentTrigger.Manual,
            Specification = new DeploymentSpecification
            {
                Name = "frontend",
                ContainerImage = "docker.io/myorg/front:2.1.0@sha256:11223344",
                Environment = "production",
                Provider = "selectel",
                ContainerPort = 80,
                TargetConfigurationId = Guid.NewGuid(),
                TerraformModule = "container-vm-selectel:v1.0.0",
                EntrypointDomain = "app.example.com",
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

        await harness.Bus.Publish(request);

        var consumerHarness = harness.GetConsumerHarness<DeploymentRequestedConsumer>();
        Assert.True(await consumerHarness.Consumed.Any<DeploymentRequested>());

        // Verify status events were published by orchestrator
        Assert.True(await harness.Published.Any<JobStatusChanged>(x => x.Context.Message.Status == JobStatus.Succeeded));
    }
}
