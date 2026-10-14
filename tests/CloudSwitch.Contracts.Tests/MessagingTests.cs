using CloudSwitch.Contracts.Events;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudSwitch.Contracts.Tests;

public class MessagingTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Health_event_reaches_recovery_and_notification_consumers_independently()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(x =>
            {
                x.SetTestTimeouts(testTimeout: TimeSpan.FromSeconds(10), testInactivityTimeout: TimeSpan.FromSeconds(3));
                x.AddConsumer<HealthRecoveryConsumer>();
                x.AddConsumer<HealthNotificationConsumer>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var health = SerializationTests.Read<DeploymentHealthChanged>("deployment-health-changed.v1.json");
        await harness.Bus.Publish(health);

        var recovery = harness.GetConsumerHarness<HealthRecoveryConsumer>();
        var notifications = harness.GetConsumerHarness<HealthNotificationConsumer>();
        Assert.True(await recovery.Consumed.Any<DeploymentHealthChanged>());
        Assert.True(await notifications.Consumed.Any<DeploymentHealthChanged>());

        var recoveryContext = Assert.Single(recovery.Consumed.Select<DeploymentHealthChanged>()).Context;
        var notificationContext = Assert.Single(notifications.Consumed.Select<DeploymentHealthChanged>()).Context;
        Assert.Equal(health, recoveryContext.Message);
        Assert.Equal(health, notificationContext.Message);
        Assert.Equal(health.CorrelationId, recoveryContext.CorrelationId);
        Assert.Equal(health.CorrelationId, notificationContext.CorrelationId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Request_and_status_reach_consumers_with_payload_and_correlation_intact()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(x =>
            {
                x.SetTestTimeouts(testTimeout: TimeSpan.FromSeconds(10), testInactivityTimeout: TimeSpan.FromSeconds(3));
                x.AddConsumer<RequestConsumer>();
                x.AddConsumer<StatusConsumer>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var request = SerializationTests.Read<DeploymentRequested>("deployment-requested.v1.json");
        await harness.Bus.Publish(request);

        var requests = harness.GetConsumerHarness<RequestConsumer>();
        Assert.True(await requests.Consumed.Any<DeploymentRequested>());
        var receivedRequest = Assert.Single(requests.Consumed.Select<DeploymentRequested>()).Context;
        Assert.Equal(request, receivedRequest.Message);
        Assert.Equal(request.CorrelationId, receivedRequest.CorrelationId);

        var statuses = harness.GetConsumerHarness<StatusConsumer>();
        Assert.True(await statuses.Consumed.Any<JobStatusChanged>());
        var receivedStatus = Assert.Single(statuses.Consumed.Select<JobStatusChanged>()).Context;
        Assert.Equal(request.JobId, receivedStatus.Message.JobId);
        Assert.Equal(request.DeploymentId, receivedStatus.Message.DeploymentId);
        Assert.Equal(request.UserId, receivedStatus.Message.UserId);
        Assert.Equal(request.CorrelationId, receivedStatus.Message.CorrelationId);
        Assert.Equal(request.CorrelationId, receivedStatus.CorrelationId);
        Assert.Equal(JobStatus.Pending, receivedStatus.Message.PreviousStatus);
        Assert.Equal(JobStatus.Running, receivedStatus.Message.Status);
        Assert.Equal(1, receivedStatus.Message.Attempt);
        Assert.Equal(1, receivedStatus.Message.Sequence);
    }

    // Test-only consumers demonstrate interoperability; job processing belongs to Worker.
    public sealed class RequestConsumer : IConsumer<DeploymentRequested>
    {
        public Task Consume(ConsumeContext<DeploymentRequested> context) =>
            context.Publish(new JobStatusChanged
            {
                EventId = Guid.NewGuid(),
                CorrelationId = context.Message.CorrelationId,
                OccurredAt = DateTimeOffset.UtcNow,
                JobId = context.Message.JobId,
                DeploymentId = context.Message.DeploymentId,
                UserId = context.Message.UserId,
                PreviousStatus = JobStatus.Pending,
                Status = JobStatus.Running,
                Attempt = 1,
                Sequence = 1
            });
    }

    public sealed class StatusConsumer : IConsumer<JobStatusChanged>
    {
        public Task Consume(ConsumeContext<JobStatusChanged> context) => Task.CompletedTask;
    }

    public sealed class HealthRecoveryConsumer : IConsumer<DeploymentHealthChanged>
    {
        public Task Consume(ConsumeContext<DeploymentHealthChanged> context) => Task.CompletedTask;
    }

    public sealed class HealthNotificationConsumer : IConsumer<DeploymentHealthChanged>
    {
        public Task Consume(ConsumeContext<DeploymentHealthChanged> context) => Task.CompletedTask;
    }
}
