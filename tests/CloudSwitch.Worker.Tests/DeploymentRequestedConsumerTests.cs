using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Worker.Consumers;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Orchestration;
using CloudSwitch.Worker.Recovery;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudSwitch.Worker.Tests;

public sealed class DeploymentRequestedConsumerTests
{
    [Fact]
    public async Task Consumer_processes_a_persisted_request_and_deduplicates_redelivery()
    {
        await using var provider = new ServiceCollection()
            .AddOptions().AddSingleton<IInfrastructureExecutor, DeterministicInfrastructureExecutor>()
            .AddSingleton<ITargetHealthValidator, DeterministicHealthValidator>()
            .AddSingleton<ITrafficRoutingService, DeterministicTrafficRoutingService>()
            .AddSingleton(service => new InMemoryJobLeaseStore(service.GetRequiredService<IBus>()))
            .AddSingleton<IJobLeaseStore>(service => service.GetRequiredService<InMemoryJobLeaseStore>())
            .AddSingleton<IMigrationOrchestrator, MigrationOrchestrator>()
            .AddMassTransitTestHarness(configuration => configuration.AddConsumer<DeploymentRequestedConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var request = WorkerTestContext.Request();
        provider.GetRequiredService<InMemoryJobLeaseStore>().RegisterJob(request);
        await harness.Bus.Publish(request);
        Assert.True(await harness.Published.Any<JobStatusChanged>(item => item.Context.Message.Status == JobStatus.Succeeded));
        await harness.Bus.Publish(request);
        Assert.True(await harness.GetConsumerHarness<DeploymentRequestedConsumer>().Consumed.Any<DeploymentRequested>());
        Assert.Single(harness.Published.Select<JobStatusChanged>(), item => item.Context.Message.Status == JobStatus.Succeeded);
    }
}
