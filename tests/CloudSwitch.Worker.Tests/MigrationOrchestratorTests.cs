using System.Text.Json;
using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Orchestration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CloudSwitch.Worker.Tests;

public sealed class MigrationOrchestratorTests
{
    [Fact]
    public async Task Happy_path_completes_all_phases_with_monotonic_sequence_and_original_correlation()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request);
        var outcome = await context.Orchestrator().ExecuteMigrationAsync(request);
        Assert.True(outcome.IsSuccess);
        Assert.Equal(JobStatus.Succeeded, outcome.FinalStatus);
        Assert.Equal("https://pay.example.com", outcome.PublicEntrypoint);
        var events = context.Harness.Published.Select<JobStatusChanged>().Select(item => item.Context.Message).ToList();
        Assert.Equal(new[] { MigrationPhase.Provisioning, MigrationPhase.TargetValidation, MigrationPhase.TrafficCutover,
            MigrationPhase.EntrypointVerification, MigrationPhase.Completed }, events.Select(item => item.Phase));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, events.Select(item => item.Sequence));
        Assert.All(events, item => Assert.Equal(request.CorrelationId, item.CorrelationId));
        Assert.Equal(CutoverOutcome.Verified, events.Last().Cutover);
    }

    [Fact]
    public async Task Duplicate_delivery_does_not_execute_a_terminal_job_twice()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request);
        var orchestrator = context.Orchestrator();
        Assert.True((await orchestrator.ExecuteMigrationAsync(request)).IsSuccess);
        Assert.Equal("JOB_NOT_ACQUIRED", (await orchestrator.ExecuteMigrationAsync(request)).ErrorCode);
        Assert.Equal(5, context.Harness.Published.Select<JobStatusChanged>().Count());
    }

    [Fact]
    public async Task Target_health_failure_prevents_traffic_cutover()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request);
        var outcome = await context.Orchestrator(health: new DeterministicHealthValidator(false)).ExecuteMigrationAsync(request);
        Assert.False(outcome.IsSuccess);
        Assert.Equal("TARGET_HEALTH_CHECK_FAILED", outcome.ErrorCode);
        Assert.DoesNotContain(context.Harness.Published.Select<JobStatusChanged>(), item => item.Context.Message.Phase == MigrationPhase.TrafficCutover);
    }

    [Fact]
    public async Task Provisioning_failure_halts_before_health_or_cutover()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        request = request with { Specification = request.Specification with { TerraformModule = "unapproved-module" } };
        context.Store.RegisterJob(request);
        var outcome = await context.Orchestrator().ExecuteMigrationAsync(request);
        Assert.Equal("PROVISIONING_FAILED", outcome.ErrorCode);
        Assert.Equal(MigrationPhase.Provisioning, outcome.FinalPhase);
        Assert.Equal(2, context.Harness.Published.Select<JobStatusChanged>().Count());
    }

    [Fact]
    public async Task Legacy_snapshot_fails_without_provisioning()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        request = request with { Specification = request.Specification with { TargetConfigurationId = null } };
        context.Store.RegisterJob(request);
        var outcome = await context.Orchestrator().ExecuteMigrationAsync(request);
        Assert.Equal("MISSING_MIGRATION_SNAPSHOT", outcome.ErrorCode);
        Assert.Equal(MigrationPhase.Pending, outcome.FinalPhase);
    }

    [Fact]
    public async Task Host_cancellation_leaves_job_available_for_redelivery()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request);
        var orchestrator = context.Orchestrator();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.ExecuteMigrationAsync(request, cancellation.Token));
        Assert.Empty(context.Harness.Published.Select<JobStatusChanged>());
        context.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await orchestrator.ExecuteMigrationAsync(request)).IsSuccess);
    }

    [Fact]
    public async Task Unexpected_exception_exposes_no_raw_secrets_in_events_or_logs()
    {
        await using var context = await WorkerTestContext.StartAsync();
        var request = WorkerTestContext.Request();
        context.Store.RegisterJob(request);
        var logger = new CaptureLogger();
        var outcome = await context.Orchestrator(executor: new ThrowingExecutor(), logger: logger).ExecuteMigrationAsync(request);
        Assert.Equal("UNHANDLED_EXECUTION_ERROR", outcome.ErrorCode);
        Assert.DoesNotContain(ThrowingExecutor.Secret, JsonSerializer.Serialize(outcome));
        Assert.DoesNotContain(ThrowingExecutor.Secret, string.Join("\n", logger.Messages));
        Assert.All(context.Harness.Published.Select<JobStatusChanged>(), item =>
            Assert.DoesNotContain(ThrowingExecutor.Secret, JsonSerializer.Serialize(item.Context.Message)));
    }

    private sealed class ThrowingExecutor : IInfrastructureExecutor
    {
        public const string Secret = "synthetic-review-password";
        public Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException($"password='{Secret}'");
        public Task<ReconciliationResult> ReconcileAsync(ReconciliationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(ReconciliationResult.NotFound());
    }

    private sealed class CaptureLogger : ILogger<MigrationOrchestrator>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + exception?.ToString());
    }
}
