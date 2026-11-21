using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Orchestration;
using CloudSwitch.Worker.Recovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CloudSwitch.Worker.Tests;

public sealed class BoundedExecutionRecoveryTests
{
    [Fact]
    public async Task Job_recovery_service_isolates_failures_and_continues_recovering_other_jobs()
    {
        await using var context = await WorkerTestContext.StartAsync();

        var failingRequest = WorkerTestContext.Request();
        var healthyRequest = WorkerTestContext.Request();

        // Register both jobs as stale
        context.Store.RegisterJob(failingRequest, attempt: 0, status: JobStatus.Pending, expiresAt: context.Clock.GetUtcNow().AddMinutes(-1));
        context.Store.RegisterJob(healthyRequest, attempt: 0, status: JobStatus.Pending, expiresAt: context.Clock.GetUtcNow().AddMinutes(-1));

        var orchestrator = new MockRoutingOrchestrator(jobIdToFail: failingRequest.JobId);
        var recoveryService = new JobRecoveryService(
            context.Store,
            orchestrator,
            Options.Create(new WorkerOptions { MaxConcurrentRecoveries = 2 }),
            NullLogger<JobRecoveryService>.Instance);

        // Recover all stale jobs - should not throw even though one job throws
        await recoveryService.RecoverStaleJobsAsync();

        // Both jobs were attempted!
        Assert.Contains(failingRequest.JobId, orchestrator.AttemptedJobIds);
        Assert.Contains(healthyRequest.JobId, orchestrator.AttemptedJobIds);
    }

    [Fact]
    public async Task Job_recovery_service_bounds_concurrency_via_semaphore()
    {
        await using var context = await WorkerTestContext.StartAsync();

        var requests = Enumerable.Range(0, 5).Select(_ => WorkerTestContext.Request()).ToList();
        foreach (var req in requests)
        {
            context.Store.RegisterJob(req, attempt: 0, status: JobStatus.Pending, expiresAt: context.Clock.GetUtcNow().AddMinutes(-1));
        }

        var orchestrator = new ConcurrencyTrackingOrchestrator();
        var recoveryService = new JobRecoveryService(
            context.Store,
            orchestrator,
            Options.Create(new WorkerOptions { MaxConcurrentRecoveries = 2 }),
            NullLogger<JobRecoveryService>.Instance);

        await recoveryService.RecoverStaleJobsAsync();

        Assert.Equal(5, orchestrator.TotalExecutions);
        Assert.True(orchestrator.MaxObservedConcurrency <= 2,
            $"Observed concurrency was {orchestrator.MaxObservedConcurrency}, but expected <= 2.");
    }

    [Fact]
    public async Task Grace_period_policy_ignores_new_pending_jobs_and_recovers_aged_ones()
    {
        await using var context = await WorkerTestContext.StartAsync();

        var newJob = WorkerTestContext.Request();
        var agedJob = WorkerTestContext.Request();
        var expiredJob = WorkerTestContext.Request();

        var now = DateTimeOffset.UtcNow;

        // 1. New unleased job within grace period (created 10s ago, grace period is 60s)
        context.Store.RegisterJob(newJob with { OccurredAt = now.AddSeconds(-10) },
            attempt: 0, status: JobStatus.Pending, expiresAt: null);

        // 2. Aged unleased job past grace period (created 120s ago)
        context.Store.RegisterJob(agedJob with { OccurredAt = now.AddSeconds(-120) },
            attempt: 0, status: JobStatus.Pending, expiresAt: null);

        // 3. Expired lease job (lease expired 5s ago)
        context.Store.RegisterJob(expiredJob,
            attempt: 1, status: JobStatus.Running, expiresAt: now.AddSeconds(-5));

        var stale = await context.Store.FindStaleJobsAsync(now, limit: 10);

        // New job within grace period should NOT be in stale list
        Assert.DoesNotContain(stale, r => r.JobId == newJob.JobId);

        // Aged job past grace period SHOULD be in stale list
        Assert.Contains(stale, r => r.JobId == agedJob.JobId);

        // Expired lease job SHOULD be in stale list
        Assert.Contains(stale, r => r.JobId == expiredJob.JobId);
    }

    [Fact]
    public async Task Fair_ordering_orders_by_expiry_or_creation_ascending()
    {
        await using var context = await WorkerTestContext.StartAsync();

        var now = DateTimeOffset.UtcNow;
        var olderJob = WorkerTestContext.Request();
        var newerJob = WorkerTestContext.Request();

        context.Store.RegisterJob(olderJob with { OccurredAt = now.AddMinutes(-10) },
            attempt: 0, status: JobStatus.Pending, expiresAt: null);
        context.Store.RegisterJob(newerJob with { OccurredAt = now.AddMinutes(-5) },
            attempt: 0, status: JobStatus.Pending, expiresAt: null);

        var stale = await context.Store.FindStaleJobsAsync(now, limit: 10);

        Assert.Equal(2, stale.Count);
        Assert.Equal(olderJob.JobId, stale[0].JobId);
        Assert.Equal(newerJob.JobId, stale[1].JobId);
    }

    [Fact]
    public async Task Host_cancellation_preserves_lease_for_future_recovery_without_failing_job()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request);

        using var hostCts = new CancellationTokenSource();
        hostCts.Cancel(); // Simulate host shutdown

        var orchestrator = context.Orchestrator();

        // Host shutdown token cancellation throws OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.ExecuteMigrationAsync(request, hostCts.Token));

        // No terminal failed progress event published!
        var publishedEvents = context.Harness.Published.Select<JobStatusChanged>().ToList();
        Assert.DoesNotContain(publishedEvents, e => e.Context.Message.Status == JobStatus.Failed);
    }

    [Fact]
    public async Task Routing_reconciliation_converges_when_dns_already_points_to_target()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request);

        // Routing service inspects that DNS already points to target endpoint
        var routing = new DeterministicTrafficRoutingService(
            shouldCutoverSucceed: false, // If CutoverAsync was called it would fail!
            shouldVerifySucceed: true,
            forcedInspectMatches: true);  // Inspection reports already converged!

        var orchestrator = context.Orchestrator(routing: routing);
        var outcome = await orchestrator.ExecuteMigrationAsync(request);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(MigrationPhase.Completed, outcome.FinalPhase);
        Assert.Equal(JobStatus.Succeeded, outcome.FinalStatus);
    }

    [Fact]
    public async Task Routing_conflict_detected_stops_execution_safely()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request);

        // Routing service inspects DNS and detects foreign/conflicting IP
        var routing = new DeterministicTrafficRoutingService(
            forcedConflict: true);

        var orchestrator = context.Orchestrator(routing: routing);
        var outcome = await orchestrator.ExecuteMigrationAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Equal("CONFLICTING_ROUTING_STATE", outcome.ErrorCode);
        Assert.Equal(MigrationPhase.TrafficCutover, outcome.FinalPhase);
        Assert.Equal(CutoverOutcome.Uncertain, context.Harness.Published.Select<JobStatusChanged>().Last().Context.Message.Cutover);
    }

    private sealed class MockRoutingOrchestrator(Guid? jobIdToFail = null) : IMigrationOrchestrator
    {
        public List<Guid> AttemptedJobIds { get; } = [];

        public Task<MigrationOutcome> ExecuteMigrationAsync(DeploymentRequested request, CancellationToken cancellationToken = default)
        {
            AttemptedJobIds.Add(request.JobId);
            if (jobIdToFail.HasValue && request.JobId == jobIdToFail.Value)
            {
                throw new InvalidOperationException("Simulated job execution crash.");
            }
            return Task.FromResult(MigrationOutcome.Succeeded("http://10.240.0.1:8080", "https://app.test", 1));
        }
    }

    private sealed class ConcurrencyTrackingOrchestrator : IMigrationOrchestrator
    {
        private int _currentConcurrency;
        public int MaxObservedConcurrency { get; private set; }
        public int TotalExecutions { get; private set; }

        public async Task<MigrationOutcome> ExecuteMigrationAsync(DeploymentRequested request, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _currentConcurrency);
            lock (this)
            {
                if (current > MaxObservedConcurrency)
                    MaxObservedConcurrency = current;
                TotalExecutions++;
            }

            try
            {
                await Task.Delay(100, cancellationToken);
                return MigrationOutcome.Succeeded("http://10.240.0.1:8080", "https://app.test", 1);
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrency);
            }
        }
    }
}
