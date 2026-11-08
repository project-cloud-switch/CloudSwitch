using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudSwitch.Api.Contracts;
using CloudSwitch.Api.Authentication;
using CloudSwitch.Contracts;
using CloudSwitch.Persistence;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Domain = CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Api.Tests;

public sealed class PersistenceTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private HttpClient Client(string subject = "owner")
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Factory.CreateToken(subject: subject));
        return client;
    }

    private static DeploymentRequest Definition() => new()
    {
        Name = "demo",
        Definition = new DeploymentDefinitionDto
        {
            Environment = "development", SourceProvider = "yandex", SourceEndpoint = new Uri("https://app.example.test"),
            Application = new ApplicationConfiguration
            {
                Image = "nginx@sha256:" + new string('a', 64), Port = 80, CpuMillicores = 500, MemoryMegabytes = 256,
                SecretReferences = new() { ["API_KEY"] = "secret://demo/api-key" }
            },
            HealthCheck = new HealthCheckPolicy { Path = "/health", IntervalSeconds = 10, TimeoutSeconds = 3, FailureThreshold = 3, RecoveryThreshold = 2 },
            Traffic = new TrafficConfiguration { Driver = "simulation", RecordName = "app.example.test" }
        }
    };

    private static async Task<DeploymentResponse> CreateDeploymentAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/deployments", Definition());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DeploymentResponse>())!;
    }

    private static async Task<TargetResponse> CreateTargetAsync(HttpClient client, Guid deploymentId)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/deployments/{deploymentId}/targets",
            new TargetRequest { Provider = "local", ModuleId = "local-stateless-v1", Inputs = new() { ["region"] = "test" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TargetResponse>())!;
    }

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid deploymentId, Guid targetId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/deployments/{deploymentId}/jobs")
        { Content = JsonContent.Create(new JobRequest(targetId)) };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    [DatabaseFact]
    public async Task Concurrent_first_requests_resolve_one_internal_user()
    {
        using var client = Client("new-" + Guid.NewGuid());
        var profiles = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetFromJsonAsync<Account>("/api/v1/me")));
        Assert.Single(profiles.Select(x => x!.Id).Distinct());
        await using var db = fixture.CreateDb();
        Assert.Equal(1, await db.Users.CountAsync(x => x.Subject == profiles[0]!.Subject));
    }

    [DatabaseFact]
    public async Task Ownership_is_enforced_for_deployments_targets_and_jobs()
    {
        using var owner = Client();
        using var stranger = Client("stranger");
        var deployment = await CreateDeploymentAsync(owner);
        var target = await CreateTargetAsync(owner, deployment.Id);
        using var submitted = await SubmitAsync(owner, deployment.Id, target.Id, Guid.NewGuid().ToString());
        var job = await submitted.Content.ReadFromJsonAsync<JobResponse>();
        foreach (var path in new[] { $"/api/v1/deployments/{deployment.Id}", $"/api/v1/deployments/{deployment.Id}/targets",
            $"/api/v1/deployments/{deployment.Id}/health", $"/api/v1/jobs/{job!.Id}", $"/api/v1/jobs/{job.Id}/events" })
        {
            using var response = await stranger.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var foreignSubmission = await SubmitAsync(stranger, deployment.Id, target.Id, "foreign");
        Assert.Equal(HttpStatusCode.NotFound, foreignSubmission.StatusCode);
        Assert.Empty((await stranger.GetFromJsonAsync<List<DeploymentResponse>>("/api/v1/deployments"))!);
        Assert.Empty((await stranger.GetFromJsonAsync<List<JobResponse>>("/api/v1/jobs"))!);
    }

    [DatabaseFact]
    public async Task Updates_require_current_etag_and_deletion_preserves_history()
    {
        using var client = Client();
        var deployment = await CreateDeploymentAsync(client);
        using var missing = await client.PutAsJsonAsync($"/api/v1/deployments/{deployment.Id}", Definition());
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        client.DefaultRequestHeaders.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        using var updated = await client.PutAsJsonAsync($"/api/v1/deployments/{deployment.Id}", Definition() with { Name = "updated" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("\"2\"", updated.Headers.ETag?.Tag);
        using var stale = await client.PutAsJsonAsync($"/api/v1/deployments/{deployment.Id}", Definition());
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        client.DefaultRequestHeaders.IfMatch.Clear();
        client.DefaultRequestHeaders.IfMatch.Add(new EntityTagHeaderValue("\"2\""));
        using var deleted = await client.DeleteAsync($"/api/v1/deployments/{deployment.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await client.GetAsync($"/api/v1/deployments/{deployment.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        await using var db = fixture.CreateDb();
        Assert.True((await db.Deployments.FindAsync(deployment.Id))!.IsDeleted);
    }

    [DatabaseFact]
    public async Task Revision_headers_keep_exact_matching_and_ownership_checks()
    {
        using var owner = Client();
        using var stranger = Client("revision-stranger");
        var deployment = await CreateDeploymentAsync(owner);
        foreach (var value in new[] { "1", "\"01\"", "W/\"1\"", "*", "\"1\", \"2\"" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/deployments/{deployment.Id}")
            {
                Content = JsonContent.Create(Definition())
            };
            request.Headers.TryAddWithoutValidation("If-Match", value);
            using var response = await owner.SendAsync(request);
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        }

        using var foreignUpdate = await stranger.PutAsJsonAsync($"/api/v1/deployments/{deployment.Id}", Definition());
        Assert.Equal(HttpStatusCode.NotFound, foreignUpdate.StatusCode);
    }

    [DatabaseFact]
    public async Task Unsafe_or_incomplete_configuration_is_rejected()
    {
        using var client = Client();
        var valid = Definition();
        foreach (var definition in new[]
        {
            valid.Definition with { SourceEndpoint = new Uri("http://169.254.169.254/latest/meta-data") },
            valid.Definition with { SourceProvider = "yandex\n" },
            valid.Definition with { Application = valid.Definition.Application with { Image = "nginx:latest" } },
            valid.Definition with { Application = valid.Definition.Application with { Image = valid.Definition.Application.Image + "\n" } },
            valid.Definition with { Application = valid.Definition.Application with { Settings = new() { ["KEY\n"] = "value" } } },
            valid.Definition with { HealthCheck = valid.Definition.HealthCheck with { FailureThreshold = 0 } },
            valid.Definition with { Recovery = new RecoveryPolicy { Mode = RecoveryMode.Automatic } },
            valid.Definition with { Application = valid.Definition.Application with { SecretReferences = new() { ["KEY"] = "plain-text-password" } } },
            valid.Definition with { Application = valid.Definition.Application with { SecretReferences = new() { ["KEY"] = "secret://demo/key\n" } } }
        })
        {
            using var response = await client.PostAsJsonAsync("/api/v1/deployments", valid with { Definition = definition });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.NotEmpty(problem.GetProperty("errors").EnumerateObject());
            Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        }
        var deployment = await CreateDeploymentAsync(client);
        using var arbitraryModule = await client.PostAsJsonAsync($"/api/v1/deployments/{deployment.Id}/targets",
            new TargetRequest { Provider = "local", ModuleId = "arbitrary-shell-module" });
        Assert.Equal(HttpStatusCode.BadRequest, arbitraryModule.StatusCode);
        using var disallowedInput = await client.PostAsJsonAsync($"/api/v1/deployments/{deployment.Id}/targets",
            new TargetRequest { Provider = "local", ModuleId = "local-stateless-v1", Inputs = new() { ["password"] = "inline" } });
        Assert.Equal(HttpStatusCode.BadRequest, disallowedInput.StatusCode);
    }

    [DatabaseFact]
    public async Task Job_and_outbox_are_saved_without_a_broker_and_retries_are_idempotent()
    {
        using var client = Client();
        var deployment = await CreateDeploymentAsync(client);
        var target = await CreateTargetAsync(client, deployment.Id);
        var otherTarget = await CreateTargetAsync(client, deployment.Id);
        using var accepted = await SubmitAsync(client, deployment.Id, target.Id, "one-key");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var job = (await accepted.Content.ReadFromJsonAsync<JobResponse>())!;
        using var retry = await SubmitAsync(client, deployment.Id, target.Id, "one-key");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(job.Id, (await retry.Content.ReadFromJsonAsync<JobResponse>())!.Id);
        using var reused = await SubmitAsync(client, deployment.Id, otherTarget.Id, "one-key");
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        using var concurrent = await SubmitAsync(client, deployment.Id, target.Id, "another-key");
        Assert.Equal(HttpStatusCode.Conflict, concurrent.StatusCode);
        await using var db = fixture.CreateDb();
        Assert.Equal(1, await db.Jobs.CountAsync(x => x.DeploymentId == deployment.Id));
        var persisted = await db.Jobs.SingleAsync(x => x.Id == job.Id);
        var outbox = await db.Set<OutboxMessage>().SingleAsync(x => x.MessageId == persisted.Request.EventId);
        Assert.Equal(job.CorrelationId, outbox.CorrelationId);
        Assert.Contains(job.Id.ToString(), outbox.Body);
        Assert.Equal(1, job.Specification.Migration!.ConfigurationRevision);
        Assert.Equal(target.Configuration.ModuleRevision, job.Specification.Migration.Target.ModuleRevision);
        Assert.Equal(DeploymentHealthStatus.Unknown,
            (await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/deployments/{deployment.Id}/health")).GetProperty("status").Deserialize<DeploymentHealthStatus>());
        using var frozen = await client.PutAsJsonAsync($"/api/v1/deployments/{deployment.Id}", Definition());
        Assert.Equal((HttpStatusCode)428, frozen.StatusCode);
        client.DefaultRequestHeaders.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        using var activeUpdate = await client.PutAsJsonAsync($"/api/v1/deployments/{deployment.Id}", Definition());
        Assert.Equal(HttpStatusCode.Conflict, activeUpdate.StatusCode);
    }

    [DatabaseFact]
    public async Task Concurrent_submissions_with_the_same_key_create_one_job_and_message()
    {
        using var client = Client();
        var deployment = await CreateDeploymentAsync(client);
        var target = await CreateTargetAsync(client, deployment.Id);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SubmitAsync(client, deployment.Id, target.Id, "concurrent-key")));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Accepted);
        Assert.All(responses, x => Assert.True(x.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK));
        var jobs = await Task.WhenAll(responses.Select(x => x.Content.ReadFromJsonAsync<JobResponse>()));
        Assert.Single(jobs.Select(x => x!.Id).Distinct());
        foreach (var response in responses) response.Dispose();
        await using var db = fixture.CreateDb();
        var persisted = await db.Jobs.SingleAsync(x => x.DeploymentId == deployment.Id);
        Assert.Equal(1, await db.Set<OutboxMessage>().CountAsync(x => x.MessageId == persisted.Request.EventId));
    }

    [DatabaseFact]
    public async Task Concurrent_submissions_with_different_keys_cannot_start_two_migrations()
    {
        using var client = Client();
        var deployment = await CreateDeploymentAsync(client);
        var target = await CreateTargetAsync(client, deployment.Id);
        var responses = await Task.WhenAll(SubmitAsync(client, deployment.Id, target.Id, "race-a"), SubmitAsync(client, deployment.Id, target.Id, "race-b"));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Accepted);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in responses) response.Dispose();
    }

    [DatabaseFact]
    public async Task Editing_configuration_does_not_change_an_existing_job_snapshot()
    {
        using var client = Client();
        var deployment = await CreateDeploymentAsync(client);
        var target = await CreateTargetAsync(client, deployment.Id);
        using var first = await SubmitAsync(client, deployment.Id, target.Id, "snapshot");
        var original = (await first.Content.ReadFromJsonAsync<JobResponse>())!;
        await using (var db = fixture.CreateDb())
            await db.Jobs.Where(x => x.Id == original.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, Domain.JobStatus.Failed));
        client.DefaultRequestHeaders.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        var updatedDefinition = Definition();
        updatedDefinition = updatedDefinition with { Definition = updatedDefinition.Definition with
        { Application = updatedDefinition.Definition.Application with { Image = "nginx@sha256:" + new string('b', 64) } } };
        using var changed = await client.PutAsJsonAsync($"/api/v1/deployments/{deployment.Id}", updatedDefinition);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var retry = await SubmitAsync(client, deployment.Id, target.Id, "snapshot");
        var replayed = (await retry.Content.ReadFromJsonAsync<JobResponse>())!;
        Assert.Equal(original.Id, replayed.Id);
        Assert.Equal(original.Specification.ContainerImage, replayed.Specification.ContainerImage);
        Assert.Equal(1, replayed.Specification.Migration!.ConfigurationRevision);
    }

    [DatabaseFact]
    public async Task Save_failure_rolls_back_both_job_and_outgoing_message()
    {
        using var client = Client();
        var deployment = await CreateDeploymentAsync(client);
        var target = await CreateTargetAsync(client, deployment.Id);
        await using var db = fixture.CreateDb();
        var before = await db.Set<OutboxMessage>().CountAsync();
        using var app = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<CloudSwitchDbContext>(options => options.AddInterceptors(new FailJobSave()))));
        using var failing = app.CreateClient();
        failing.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        using var response = await SubmitAsync(failing, deployment.Id, target.Id, "failed-save");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(await db.Jobs.AnyAsync(x => x.DeploymentId == deployment.Id));
        Assert.Equal(before, await db.Set<OutboxMessage>().CountAsync());
    }

    [DatabaseFact]
    public async Task Automatic_recovery_rechecks_policy_and_revision_and_deduplicates_failed_incidents()
    {
        using var client = Client();
        var deployment = await CreateDeploymentAsync(client);
        var target = await CreateTargetAsync(client, deployment.Id);
        var incident = Domain.Incident.Open(deployment.Id, DateTimeOffset.UtcNow);
        var incidentId = incident.Id;
        await using (var db = fixture.CreateDb())
        {
            db.Incidents.Add(incident);
            db.Health.Add(Domain.HealthObservation.Record(deployment.Id, 1, 0, 3,
                Domain.DeploymentHealthStatus.Unhealthy, incidentId, 3, 0, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var command = new SubmitJob(deployment.Id, deployment.UserId, target.Id, Domain.JobTrigger.Automatic, null,
            incidentId, Guid.NewGuid(), 3);
        // NotifyOnly does not create a job even with a confirmed failure.
        await Assert.ThrowsAsync<OperationRejectedException>(() => SubmitAutomaticAsync(command));
        client.DefaultRequestHeaders.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        var updated = Definition();
        updated = updated with { Definition = updated.Definition with
        { Recovery = new RecoveryPolicy { Mode = RecoveryMode.Automatic, TargetConfigurationId = target.Id } } };
        using var policy = await client.PutAsJsonAsync($"/api/v1/deployments/{deployment.Id}", updated);
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
        // The observation is now stale because the configuration revision changed.
        await Assert.ThrowsAsync<OperationRejectedException>(() => SubmitAutomaticAsync(command));
        await using (var db = fixture.CreateDb())
            await db.Health.Where(x => x.DeploymentId == deployment.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ConfigurationRevision, 2));
        var accepted = await SubmitAutomaticAsync(command);
        Assert.True(accepted.Created);
        await using (var db = fixture.CreateDb())
            await db.Jobs.Where(x => x.Id == accepted.Job.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, Domain.JobStatus.Failed));
        var replayed = await SubmitAutomaticAsync(command);
        Assert.False(replayed.Created);
        Assert.Equal(accepted.Job.Id, replayed.Job.Id);
        await using var verification = fixture.CreateDb();
        Assert.Equal(1, await verification.Jobs.CountAsync(x => x.DeploymentId == deployment.Id));
        Assert.Equal(1, await verification.Set<OutboxMessage>().CountAsync(x => x.MessageId == accepted.Job.Request.EventId));
    }

    [DatabaseFact]
    public async Task Database_active_job_index_protects_against_writers_bypassing_the_submission_service()
    {
        using var client = Client();
        var deployment = await CreateDeploymentAsync(client);
        var target = await CreateTargetAsync(client, deployment.Id);
        using var response = await SubmitAsync(client, deployment.Id, target.Id, "first-active");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await using var db = fixture.CreateDb();
        var first = await db.Jobs.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment.Id);
        var second = Domain.Job.Create(first.DeploymentId, first.UserId, first.Request.Specification,
            Domain.JobTrigger.Manual, "bypassed-lock", null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        db.Jobs.Add(second);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(failure.InnerException);
        Assert.Equal("UX_Jobs_ActiveDeployment", postgres.ConstraintName);
    }

    private async Task<SubmissionResult> SubmitAutomaticAsync(SubmitJob command)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobSubmissionService>().SubmitAsync(command, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Database_roles_preserve_service_write_ownership()
    {
        foreach (var (role, sql) in new[]
        {
            ("cloudswitch_api", "UPDATE \"Jobs\" SET \"Status\" = 'Running' WHERE false"),
            ("cloudswitch_worker", "UPDATE \"Deployments\" SET \"Name\" = 'changed' WHERE false"),
            ("cloudswitch_worker", "UPDATE \"Jobs\" SET \"Request\" = '{}'::jsonb WHERE false")
        })
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var setRole = new NpgsqlCommand("SET LOCAL ROLE " + role, connection, transaction))
                await setRole.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }
    }

    private sealed class FailJobSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Domain.Job>().Any(x => x.State == EntityState.Added))
                throw new InvalidOperationException("Injected storage failure.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
