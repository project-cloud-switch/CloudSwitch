using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Orchestration;
using CloudSwitch.Worker.Recovery;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudSwitch.Worker.Tests;

internal sealed class WorkerTestContext : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    public ITestHarness Harness { get; }
    public TestClock Clock { get; } = new();
    public InMemoryJobLeaseStore Store { get; }
    public DeterministicInfrastructureExecutor Executor { get; } = new();

    private WorkerTestContext(ServiceProvider provider)
    {
        this.provider = provider;
        Harness = provider.GetRequiredService<ITestHarness>();
        Store = new InMemoryJobLeaseStore(Harness.Bus, Clock);
    }

    public static async Task<WorkerTestContext> StartAsync()
    {
        var provider = new ServiceCollection().AddMassTransitTestHarness().BuildServiceProvider(true);
        var context = new WorkerTestContext(provider);
        await context.Harness.Start();
        return context;
    }

    public MigrationOrchestrator Orchestrator(IInfrastructureExecutor? executor = null, ITargetHealthValidator? health = null,
        ITrafficRoutingService? routing = null, IJobLeaseStore? store = null, ILogger<MigrationOrchestrator>? logger = null)
        => new(executor ?? Executor, health ?? new DeterministicHealthValidator(), routing ?? new DeterministicTrafficRoutingService(),
            store ?? Store, Options.Create(new WorkerOptions()), logger ?? NullLogger<MigrationOrchestrator>.Instance);

    public static DeploymentRequested Request(Guid? deploymentId = null) => new()
    {
        EventId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow,
        JobId = Guid.NewGuid(), DeploymentId = deploymentId ?? Guid.NewGuid(), UserId = Guid.NewGuid(),
        Trigger = DeploymentTrigger.Automatic, IncidentId = Guid.NewGuid(),
        Specification = new DeploymentSpecification
        {
            Name = "payments", ContainerImage = "docker.io/myorg/pay@sha256:" + new string('a', 64),
            Environment = "development", Provider = "selectel",
            ContainerPort = 8080, CpuCores = 1, MemoryMb = 512,
            TargetConfigurationId = Guid.NewGuid(), TerraformModule = "container-vm-selectel:v1.0.0",
            SecretReferences = new Dictionary<string, string> { ["DB_PASSWORD"] = "vault://test/db#password" },
            HealthCheck = new HealthCheckPolicy { Path = "/healthz", IntervalSeconds = 10, TimeoutSeconds = 3, FailureThreshold = 3, RecoveryThreshold = 2 },
            EntrypointDomain = "pay.example.com", TrafficRoutingProvider = "simulation"
        }
    };

    public ValueTask DisposeAsync() => provider.DisposeAsync();
}

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now = now.Add(duration);
}
