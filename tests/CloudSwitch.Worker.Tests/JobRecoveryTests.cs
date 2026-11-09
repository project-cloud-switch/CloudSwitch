using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Orchestration;
using CloudSwitch.Worker.Recovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CloudSwitch.Worker.Tests;

public sealed class JobRecoveryTests
{
    [Fact]
    public async Task Active_lease_cannot_be_acquired_twice_even_by_the_same_worker()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request);
        Assert.NotNull(await context.Store.TryAcquireAsync(request, "worker", TimeSpan.FromMinutes(2), 3));
        Assert.Null(await context.Store.TryAcquireAsync(request, "worker", TimeSpan.FromMinutes(2), 3));
        Assert.Null(await context.Store.TryAcquireAsync(request, "another", TimeSpan.FromMinutes(2), 3));
    }

    [Fact]
    public async Task Expired_lease_acquisition_increments_attempt_and_fence_for_the_same_owner()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request);
        var old = Assert.IsType<JobLease>(await context.Store.TryAcquireAsync(request, "worker", TimeSpan.FromSeconds(3), 3));
        context.Clock.Advance(TimeSpan.FromSeconds(4));
        var next = Assert.IsType<JobLease>(await context.Store.TryAcquireAsync(request, "worker", TimeSpan.FromMinutes(2), 3));
        Assert.Equal(old.Attempt + 1, next.Attempt);
        Assert.True(next.FencingToken > old.FencingToken);
        Assert.Null(await context.Store.RecordProgressAsync(old, JobStatus.Running, MigrationPhase.TrafficCutover, CutoverOutcome.Uncertain));
        Assert.False(await context.Store.RenewAsync(old, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public async Task Retry_exhaustion_records_one_terminal_failure()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request, attempt: 3, status: JobStatus.Running, expiresAt: context.Clock.GetUtcNow().AddMinutes(-1));
        Assert.Null(await context.Store.TryAcquireAsync(request, "recovery", TimeSpan.FromMinutes(2), 3));
        Assert.Null(await context.Store.TryAcquireAsync(request, "recovery", TimeSpan.FromMinutes(2), 3));
        var failed = Assert.Single(context.Harness.Published.Select<JobStatusChanged>()).Context.Message;
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal("RETRY_LIMIT_EXHAUSTED", failed.ErrorCode);
    }

    [Fact]
    public async Task Recovery_continues_sequence_and_preserves_original_message_identity()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request, attempt: 1, sequence: 7, status: JobStatus.Running,
            phase: MigrationPhase.Provisioning, expiresAt: context.Clock.GetUtcNow().AddMinutes(-1), owner: "previous");
        var recovery = new JobRecoveryService(context.Store, context.Orchestrator(), Options.Create(new WorkerOptions()), NullLogger<JobRecoveryService>.Instance);
        await recovery.RecoverStaleJobsAsync();
        var events = context.Harness.Published.Select<JobStatusChanged>().Select(item => item.Context.Message).ToList();
        Assert.Equal(new long[] { 8, 9, 10, 11, 12 }, events.Select(item => item.Sequence));
        Assert.All(events, item =>
        {
            Assert.Equal(2, item.Attempt);
            Assert.Equal(request.CorrelationId, item.CorrelationId);
        });
        Assert.Equal(JobStatus.Succeeded, events.Last().Status);
    }

    [Fact]
    public async Task Conflicting_target_state_stops_recovery()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request, attempt: 1, status: JobStatus.Running, expiresAt: context.Clock.GetUtcNow().AddMinutes(-1));
        var outcome = await context.Orchestrator(executor: new ConflictingExecutor()).ExecuteMigrationAsync(request);
        Assert.Equal("CONFLICTING_TARGET_STATE", outcome.ErrorCode);
        Assert.Equal(JobStatus.Failed, outcome.FinalStatus);
    }

    [Fact]
    public async Task Interrupted_cutover_requires_reconciliation_instead_of_repeating_the_mutation()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request, attempt: 1, status: JobStatus.Running, cutover: CutoverOutcome.Applied,
            phase: MigrationPhase.EntrypointVerification, expiresAt: context.Clock.GetUtcNow().AddMinutes(-1));
        var outcome = await context.Orchestrator(executor: new ConflictingExecutor()).ExecuteMigrationAsync(request);
        Assert.Equal("UNCERTAIN_CUTOVER_STATE", outcome.ErrorCode);
        Assert.Equal(MigrationPhase.TrafficCutover, outcome.FinalPhase);
        Assert.Equal(CutoverOutcome.Uncertain, context.Harness.Published.Select<JobStatusChanged>().Last().Context.Message.Cutover);
    }

    [Fact]
    public async Task A_rejected_checkpoint_prevents_traffic_cutover()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request);
        var routing = new CountingRouting();
        var outcome = await context.Orchestrator(routing: routing, store: new RejectCutoverStore(context.Store)).ExecuteMigrationAsync(request);
        Assert.Equal("LEASE_LOST", outcome.ErrorCode);
        Assert.Equal(0, routing.CutoverCalls);
    }

    private sealed class ConflictingExecutor : IInfrastructureExecutor
    {
        public Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Provisioning must stop after a conflict.");
        public Task<ReconciliationResult> ReconcileAsync(ReconciliationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(ReconciliationResult.Conflict("CONFLICTING_TARGET_STATE", "Conflicting route."));
    }

    private sealed class CountingRouting : ITrafficRoutingService
    {
        public int CutoverCalls { get; private set; }
        public Task<RoutingResult> CutoverAsync(string domain, string endpoint, string? provider, CancellationToken cancellationToken = default)
        {
            CutoverCalls++;
            return Task.FromResult(RoutingResult.Success($"https://{domain}"));
        }
        public Task<RoutingResult> VerifyEntrypointAsync(string domain, string path, CancellationToken cancellationToken = default)
            => Task.FromResult(RoutingResult.Success($"https://{domain}"));
    }

    private sealed class RejectCutoverStore(IJobLeaseStore inner) : IJobLeaseStore
    {
        public Task<JobLease?> TryAcquireAsync(DeploymentRequested request, string owner, TimeSpan duration, int attempts, CancellationToken cancellationToken = default)
            => inner.TryAcquireAsync(request, owner, duration, attempts, cancellationToken);
        public Task<bool> RenewAsync(JobLease lease, TimeSpan duration, CancellationToken cancellationToken = default)
            => inner.RenewAsync(lease, duration, cancellationToken);
        public Task<IReadOnlyList<DeploymentRequested>> FindStaleJobsAsync(DateTimeOffset asOf, int limit, CancellationToken cancellationToken = default)
            => inner.FindStaleJobsAsync(asOf, limit, cancellationToken);
        public Task<JobStatusChanged?> RecordProgressAsync(JobLease lease, JobStatus status, MigrationPhase phase, CutoverOutcome cutover,
            Uri? endpoint = null, string? errorCode = null, string? errorMessage = null, string? stateFingerprint = null, CancellationToken cancellationToken = default)
            => phase == MigrationPhase.TrafficCutover ? Task.FromResult<JobStatusChanged?>(null)
                : inner.RecordProgressAsync(lease, status, phase, cutover, endpoint, errorCode, errorMessage, stateFingerprint, cancellationToken);
    }
}
