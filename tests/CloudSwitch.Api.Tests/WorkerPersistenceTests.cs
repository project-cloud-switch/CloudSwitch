using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudSwitch.Api.Contracts;
using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Persistence;
using CloudSwitch.Worker;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Orchestration;
using CloudSwitch.Worker.Recovery;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Domain = CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Api.Tests;

[Collection("PostgreSQL")]
[Trait("Category", "ExternalIntegration")]
public sealed class WorkerPersistenceTests(DatabaseFixture fixture)
{
    private HttpClient Client()
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Factory.CreateToken());
        return client;
    }

    private async Task<DeploymentRequested> SubmitAsync(HttpClient client)
    {
        var deployment = await PersistenceTests.CreateDeploymentAsync(client);
        var target = await PersistenceTests.CreateTargetAsync(client, deployment.Id);
        using var response = await PersistenceTests.SubmitAsync(client, deployment.Id, target.Id, Guid.NewGuid().ToString());
        response.EnsureSuccessStatusCode();
        var accepted = (await response.Content.ReadFromJsonAsync<JobResponse>())!;
        await using var db = fixture.CreateDb();
        return (await db.Jobs.SingleAsync(job => job.Id == accepted.Id)).Request.ToContract();
    }

    private PostgresJobLeaseStore Store(IServiceProvider? services = null)
        => new((services ?? fixture.Factory.Services).GetRequiredService<IServiceScopeFactory>());

    [DatabaseFact]
    public async Task Concurrent_claims_and_expired_owners_are_fenced_and_sequence_survives_restart()
    {
        using var client = Client();
        var request = await SubmitAsync(client);
        var store = Store();
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            store.TryAcquireAsync(request, $"worker-{index}", TimeSpan.FromMinutes(2), 3)));
        var old = Assert.Single(claims.OfType<JobLease>());
        var first = await store.RecordProgressAsync(old, JobStatus.Running, MigrationPhase.Provisioning, CutoverOutcome.NotStarted);
        Assert.Equal(1, first!.Sequence);
        await using var db = fixture.CreateDb();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Jobs\" SET \"LeaseExpiresAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = {request.JobId}");
        var restarted = Store();
        var next = Assert.IsType<JobLease>(await restarted.TryAcquireAsync(request, old.WorkerId, TimeSpan.FromMinutes(2), 3));
        Assert.Equal(2, next.Attempt);
        Assert.True(next.FencingToken > old.FencingToken);
        Assert.Null(await store.RecordProgressAsync(old, JobStatus.Running, MigrationPhase.TrafficCutover, CutoverOutcome.Uncertain));
        Assert.False(await store.RenewAsync(old, TimeSpan.FromMinutes(2)));
        var second = await restarted.RecordProgressAsync(next, JobStatus.Running, MigrationPhase.TargetValidation,
            CutoverOutcome.NotStarted, new Uri("https://target.example.test"));
        Assert.Equal(2, second!.Sequence);
        Assert.Equal(request.CorrelationId, second.CorrelationId);
        Assert.Equal("https://target.example.test/", second.TargetEndpoint);
        Assert.Equal(2, await db.JobEvents.CountAsync(item => item.JobId == request.JobId));
    }

    [DatabaseFact]
    public async Task Progress_history_and_outbox_roll_back_together_after_database_write_failure()
    {
        using var client = Client();
        var request = await SubmitAsync(client);
        using var failingFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<CloudSwitchDbContext>(options => options.AddInterceptors(new FailProgressSave()))));
        var store = Store(failingFactory.Services);
        var lease = Assert.IsType<JobLease>(await store.TryAcquireAsync(request, "worker", TimeSpan.FromMinutes(2), 3));
        await using var db = fixture.CreateDb();
        var outboxBefore = await db.Set<OutboxMessage>().CountAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordProgressAsync(lease, JobStatus.Running,
            MigrationPhase.Provisioning, CutoverOutcome.NotStarted));
        var unchanged = await db.Jobs.SingleAsync(job => job.Id == request.JobId);
        Assert.Equal(Domain.JobStatus.Pending, unchanged.Status);
        Assert.Equal(0, unchanged.Sequence);
        Assert.Empty(await db.JobEvents.Where(item => item.JobId == request.JobId).ToListAsync());
        Assert.Equal(outboxBefore, await db.Set<OutboxMessage>().CountAsync());
        var retried = await Store().RecordProgressAsync(lease, JobStatus.Running, MigrationPhase.Provisioning, CutoverOutcome.NotStarted);
        Assert.Equal(1, retried!.Sequence);
        Assert.True(await db.Set<OutboxMessage>().AnyAsync(message => message.MessageId == retried.EventId));
    }

    [DatabaseFact]
    public async Task Worker_uses_frozen_request_and_api_reads_committed_progress_without_a_broker()
    {
        using var client = Client();
        var request = await SubmitAsync(client);
        var orchestrator = new MigrationOrchestrator(new DeterministicInfrastructureExecutor(), new DeterministicHealthValidator(),
            new DeterministicTrafficRoutingService(), Store(), Options.Create(new WorkerOptions { Simulation = true }),
            NullLogger<MigrationOrchestrator>.Instance);
        var tampered = request with { Specification = request.Specification with { TerraformModule = "unapproved" } };
        Assert.True((await orchestrator.ExecuteMigrationAsync(tampered)).IsSuccess);
        Assert.Equal("JOB_NOT_ACQUIRED", (await orchestrator.ExecuteMigrationAsync(request)).ErrorCode);
        var actual = await client.GetFromJsonAsync<JobResponse>($"/api/v1/jobs/{request.JobId}");
        Assert.Equal(JobStatus.Succeeded, actual!.Status);
        Assert.Equal(MigrationPhase.Completed, actual.Phase);
        Assert.Equal(CutoverOutcome.Verified, actual.Cutover);
        Assert.Equal(5, actual.Sequence);
        var history = await client.GetFromJsonAsync<List<JobStatusChanged>>($"/api/v1/jobs/{request.JobId}/events");
        Assert.NotNull(history);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, history.Select(item => item.Sequence));
        Assert.Equal(MigrationPhase.TargetValidation, history[1].Phase);
        Assert.All(history, item => Assert.Equal(request.CorrelationId, item.CorrelationId));
        await using var db = fixture.CreateDb();
        var runtime = await db.Runtimes.SingleAsync(item => item.DeploymentId == request.DeploymentId);
        Assert.Equal(1, runtime.Revision);
        Assert.Equal(actual.TargetEndpoint, runtime.Endpoint);
        foreach (var progress in history)
        {
            var outgoing = await db.Set<OutboxMessage>().SingleAsync(message => message.MessageId == progress.EventId);
            Assert.Equal(progress.CorrelationId, outgoing.CorrelationId);
        }
    }

    private sealed class FailProgressSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
            => eventData.Context!.ChangeTracker.Entries<Domain.JobEvent>().Any()
                ? throw new InvalidOperationException("Synthetic failure after writing progress.") : ValueTask.FromResult(result);
    }
}
