using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Orchestration;
using CloudSwitch.Worker.Recovery;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudSwitch.Worker.Tests;

public class JobRecoveryTests
{
    private static JobLease CreateRunningJobLease(
        Guid? jobId = null,
        DateTimeOffset? expiresAt = null,
        int attempt = 1,
        int maxAttempts = 3) => new()
    {
        JobId = jobId ?? Guid.NewGuid(),
        DeploymentId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        WorkerId = "worker-alpha",
        Attempt = attempt,
        MaxAttempts = maxAttempts,
        Status = JobStatus.Running,
        CurrentPhase = MigrationPhase.Provisioning,
        LeaseExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5),
        Specification = new DeploymentSpecification
        {
            Name = "demo-recovery",
            ContainerImage = "docker.io/myorg/demo:1.0.0@sha256:12345678",
            Environment = "production",
            Provider = "selectel",
            ContainerPort = 8080,
            TargetConfigurationId = Guid.NewGuid(),
            TerraformModule = "container-vm-selectel:v1.0.0",
            EntrypointDomain = "demo.example.com",
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
    public async Task Active_unexpired_lease_cannot_be_acquired_by_another_worker()
    {
        var store = new InMemoryJobLeaseStore();
        var job = CreateRunningJobLease(expiresAt: DateTimeOffset.UtcNow.AddMinutes(5));
        store.RegisterJob(job);

        var attempt = await store.TryAcquireOrRenewLeaseAsync(job.JobId, "worker-beta", TimeSpan.FromMinutes(2));

        Assert.Null(attempt);
    }

    [Fact]
    public async Task Expired_lease_is_acquired_by_new_worker_with_incremented_attempt()
    {
        var store = new InMemoryJobLeaseStore();
        var job = CreateRunningJobLease(
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), // expired 1 minute ago
            attempt: 1);
        store.RegisterJob(job);

        var acquired = await store.TryAcquireOrRenewLeaseAsync(job.JobId, "worker-beta", TimeSpan.FromMinutes(2));

        Assert.NotNull(acquired);
        Assert.Equal("worker-beta", acquired.WorkerId);
        Assert.Equal(2, acquired.Attempt);
        Assert.True(acquired.LeaseExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Fencing_prevents_expired_worker_from_updating_checkpoint()
    {
        var store = new InMemoryJobLeaseStore();
        var job = CreateRunningJobLease(
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            attempt: 1);
        store.RegisterJob(job);

        // worker-beta takes over the lease (attempt 2)
        var acquired = await store.TryAcquireOrRenewLeaseAsync(job.JobId, "worker-beta", TimeSpan.FromMinutes(2));
        Assert.NotNull(acquired);
        Assert.Equal(2, acquired.Attempt);

        // Old worker-alpha wakes up and tries to checkpoint with old Attempt 1
        var updateResult = await store.UpdateCheckpointAsync(
            job.JobId,
            expectedAttempt: 1, // Stale attempt!
            phase: MigrationPhase.TrafficCutover);

        Assert.False(updateResult); // Fenced out!
    }

    [Fact]
    public async Task Retry_exhaustion_marks_job_failed_requiring_manual_recovery()
    {
        var store = new InMemoryJobLeaseStore();
        var job = CreateRunningJobLease(
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            attempt: 3,
            maxAttempts: 3);
        store.RegisterJob(job);

        // Next acquire attempts to exceed max attempts 3
        var acquired = await store.TryAcquireOrRenewLeaseAsync(job.JobId, "worker-beta", TimeSpan.FromMinutes(2));

        Assert.Null(acquired);
        var stale = await store.FindStaleJobsAsync(DateTimeOffset.UtcNow);
        // Excluded from running stale jobs because it was marked Failed
        Assert.DoesNotContain(stale, x => x.JobId == job.JobId);
    }

    [Fact]
    public async Task Job_recovery_service_reconciles_and_completes_stale_job()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var store = new InMemoryJobLeaseStore();
        var executor = new DeterministicInfrastructureExecutor();
        var healthValidator = new DeterministicHealthValidator();
        var routingService = new DeterministicTrafficRoutingService();
        var orchestrator = new MigrationOrchestrator(executor, healthValidator, routingService, harness.Bus);

        var job = CreateRunningJobLease(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        store.RegisterJob(job);

        var recoveryService = new JobRecoveryService(store, executor, orchestrator, harness.Bus);
        var recoveryResults = await recoveryService.RecoverStaleJobsAsync("recovery-worker-1", TimeSpan.FromMinutes(5));

        var singleResult = Assert.Single(recoveryResults);
        Assert.Equal(job.JobId, singleResult.JobId);
        Assert.True(singleResult.Recovered);
        Assert.NotNull(singleResult.Outcome);
        Assert.True(singleResult.Outcome.IsSuccess);

        // Check that published events used Attempt = 2
        var events = harness.Published.Select<JobStatusChanged>().ToList();
        Assert.All(events, e => Assert.Equal(2, e.Context.Message.Attempt));
    }

    [Fact]
    public async Task Conflicting_reconciliation_state_stops_recovery_and_marks_failed()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var store = new InMemoryJobLeaseStore();
        // Executor returns conflict on reconciliation:
        var mockExecutor = new ConflictingInfrastructureExecutor();
        var healthValidator = new DeterministicHealthValidator();
        var routingService = new DeterministicTrafficRoutingService();
        var orchestrator = new MigrationOrchestrator(mockExecutor, healthValidator, routingService, harness.Bus);

        var job = CreateRunningJobLease(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        store.RegisterJob(job);

        var recoveryService = new JobRecoveryService(store, mockExecutor, orchestrator, harness.Bus);
        var recoveryResults = await recoveryService.RecoverStaleJobsAsync("recovery-worker-1", TimeSpan.FromMinutes(5));

        var singleResult = Assert.Single(recoveryResults);
        Assert.False(singleResult.Recovered);
        Assert.Equal("CONFLICTING_TARGET_STATE", singleResult.Reason);
    }

    private sealed class ConflictingInfrastructureExecutor : IInfrastructureExecutor
    {
        public Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProvisioningResult.Failure("CONFLICT", "Conflict", TimeSpan.Zero));

        public Task<ReconciliationResult> ReconcileAsync(ReconciliationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReconciliationResult.Conflict("CONFLICTING_TARGET_STATE", "Observed conflicting route in DNS"));
    }
}
