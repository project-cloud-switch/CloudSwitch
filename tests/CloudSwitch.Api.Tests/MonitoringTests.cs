using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudSwitch.Api.Contracts;
using CloudSwitch.Contracts;
using CloudSwitch.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Domain = CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Api.Tests;

[Collection("PostgreSQL")]
[Trait("Category", "ExternalIntegration")]
public sealed class MonitoringTests(DatabaseFixture fixture)
{
    private HttpClient Client(string? subject = null)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.Factory.CreateToken(subject: subject ?? "monitoring-" + Guid.NewGuid().ToString("N")));
        return client;
    }

    private static MonitoringUpdateRequest Monitoring(
        string path = "/health", int interval = 10, int timeout = 3, int failureThreshold = 3,
        int recoveryThreshold = 2, RecoveryMode mode = RecoveryMode.NotifyOnly, Guid? targetId = null, bool enabled = true)
        => new()
        {
            HealthCheck = new HealthCheckPolicy
            {
                Path = path, IntervalSeconds = interval, TimeoutSeconds = timeout,
                FailureThreshold = failureThreshold, RecoveryThreshold = recoveryThreshold
            },
            Recovery = new RecoveryPolicy { Mode = mode, TargetConfigurationId = targetId },
            MonitoringEnabled = enabled
        };

    private static Task<HttpResponseMessage> PutMonitoringAsync(HttpClient client, Guid id, long? revision, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/deployments/{id}/monitoring") { Content = content };
        if (revision is { } value)
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{value}\"");
        return client.SendAsync(request);
    }

    private async Task<Guid> OpenIncidentAsync(Guid deploymentId, long configurationRevision = 1)
    {
        var incident = Domain.Incident.Open(deploymentId, DateTimeOffset.UtcNow);
        await using var db = fixture.CreateDb();
        db.Incidents.Add(incident);
        db.Health.Add(Domain.HealthObservation.Record(deploymentId, configurationRevision, 0, 3,
            Domain.DeploymentHealthStatus.Unhealthy, incident.Id, 3, 0, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        return incident.Id;
    }

    private static SubmitJob AutomaticCommand(DeploymentResponse deployment, Guid targetId, Guid incidentId)
        => new(deployment.Id, deployment.UserId, targetId, Domain.JobTrigger.Automatic, null, incidentId, Guid.NewGuid(), 3);

    private async Task<SubmissionResult> SubmitAutomaticAsync(SubmitJob command)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobSubmissionService>().SubmitAsync(command, CancellationToken.None);
    }

    private async Task<bool> TrySubmitAutomaticAsync(SubmitJob command)
    {
        try { return (await SubmitAutomaticAsync(command)).Created; }
        catch (OperationRejectedException) { return false; }
    }

    private static async Task<(DeploymentHealthStatus Status, Guid? IncidentId)> ReadHealthAsync(HttpClient client, Guid id)
    {
        var json = await client.GetFromJsonAsync<JsonElement>($"/api/v1/deployments/{id}/health");
        var status = json.GetProperty("status").Deserialize<DeploymentHealthStatus>();
        Guid? incident = json.TryGetProperty("incidentId", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetGuid() : null;
        return (status, incident);
    }

    [DatabaseFact]
    public async Task Monitoring_can_change_while_a_job_is_active_and_the_job_stays_untouched()
    {
        using var client = Client();
        var deployment = await PersistenceTests.CreateDeploymentAsync(client);
        var target = await PersistenceTests.CreateTargetAsync(client, deployment.Id);
        using var submitted = await PersistenceTests.SubmitAsync(client, deployment.Id, target.Id, "monitoring-active-job");
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var job = (await submitted.Content.ReadFromJsonAsync<JobResponse>())!;
        var before = await client.GetStringAsync($"/api/v1/jobs/{job.Id}");

        using var response = await PutMonitoringAsync(client, deployment.Id, 1,
            JsonContent.Create(Monitoring(mode: RecoveryMode.Automatic, targetId: target.Id)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        Assert.Equal(before, await client.GetStringAsync($"/api/v1/jobs/{job.Id}"));
    }

    [DatabaseFact]
    public async Task Only_monitoring_fields_change()
    {
        using var client = Client();
        var userId = await PersistenceTests.UserIdAsync(client);
        var deployment = await PersistenceTests.CreateDeploymentAsync(client);

        using var response = await PutMonitoringAsync(client, deployment.Id, 1,
            JsonContent.Create(Monitoring(path: "/ready")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var original = PersistenceTests.Definition(userId);
        await using var db = fixture.CreateDb();
        var stored = await db.Deployments.AsNoTracking().SingleAsync(x => x.Id == deployment.Id);
        Assert.Equal(original.Name, stored.Name);
        Assert.Equal(original.Definition.Environment, stored.Definition.Environment);
        Assert.Equal(original.Definition.SourceProvider, stored.Definition.SourceProvider);
        Assert.Equal(original.Definition.SourceEndpoint, stored.Definition.SourceEndpoint);
        Assert.Equal(original.Definition.Application.Image, stored.Definition.Application.Image);
        Assert.Equal(original.Definition.Application.Port, stored.Definition.Application.Port);
        Assert.Equal(original.Definition.Traffic.RecordName, stored.Definition.Traffic.RecordName);
        Assert.Equal("/ready", stored.Definition.HealthCheck.Path);
        Assert.Equal(2, stored.Revision);
        Assert.Equal(2, stored.ObservationRevision);
    }

    [DatabaseFact]
    public async Task Missing_or_stale_etag_is_rejected()
    {
        using var client = Client();
        var deployment = await PersistenceTests.CreateDeploymentAsync(client);

        using var missing = await PutMonitoringAsync(client, deployment.Id, null, JsonContent.Create(Monitoring()));
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);

        using var first = await PutMonitoringAsync(client, deployment.Id, 1, JsonContent.Create(Monitoring(interval: 20)));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var stale = await PutMonitoringAsync(client, deployment.Id, 1, JsonContent.Create(Monitoring(interval: 30)));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [DatabaseFact]
    public async Task Incident_history_is_read_only_and_owner_only()
    {
        using var owner = Client();
        using var stranger = Client();
        var deployment = await PersistenceTests.CreateDeploymentAsync(owner);
        var incidentId = await OpenIncidentAsync(deployment.Id);

        var history = await owner.GetFromJsonAsync<List<IncidentResponse>>($"/api/v1/deployments/{deployment.Id}/incidents");
        var incident = Assert.Single(history!);
        Assert.Equal(incidentId, incident.Id);
        Assert.Equal(deployment.Id, incident.DeploymentId);
        Assert.Null(incident.ResolvedAt);

        using var foreign = await stranger.GetAsync($"/api/v1/deployments/{deployment.Id}/incidents");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        using var anonymous = fixture.Factory.CreateClient();
        using var unauthorized = await anonymous.GetAsync($"/api/v1/deployments/{deployment.Id}/incidents");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [DatabaseFact]
    public async Task Another_user_gets_not_found_and_anonymous_gets_unauthorized()
    {
        using var owner = Client();
        using var stranger = Client();
        var deployment = await PersistenceTests.CreateDeploymentAsync(owner);

        using var foreign = await PutMonitoringAsync(stranger, deployment.Id, 1, JsonContent.Create(Monitoring()));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        using var anonymous = fixture.Factory.CreateClient();
        using var unauthorized = await PutMonitoringAsync(anonymous, deployment.Id, 1, JsonContent.Create(Monitoring()));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [DatabaseFact]
    public async Task Invalid_bodies_are_rejected()
    {
        using var client = Client();
        var deployment = await PersistenceTests.CreateDeploymentAsync(client);
        var health = new { path = "/health", intervalSeconds = 10, timeoutSeconds = 3, failureThreshold = 3, recoveryThreshold = 2 };
        var notify = new { mode = "NotifyOnly" };
        var bodies = new object[]
        {
            new { healthCheck = health, recovery = notify, monitoringEnabled = true, application = new { } }, // лишнее поле
            new { healthCheck = health, monitoringEnabled = true },                                           // нет recovery
            new { healthCheck = health, recovery = notify },                                                  // нет monitoringEnabled
            new { healthCheck = new { path = "/health", intervalSeconds = 5, timeoutSeconds = 10, failureThreshold = 3, recoveryThreshold = 2 },
                  recovery = notify, monitoringEnabled = true },                                              // таймаут больше интервала
            new { healthCheck = new { path = "/h?x=1", intervalSeconds = 10, timeoutSeconds = 3, failureThreshold = 3, recoveryThreshold = 2 },
                  recovery = notify, monitoringEnabled = true },                                              // путь с query
            new { healthCheck = new { path = "/health", intervalSeconds = 10, timeoutSeconds = 3, failureThreshold = 0, recoveryThreshold = 2 },
                  recovery = notify, monitoringEnabled = true },                                              // порог 0
            new { healthCheck = health, recovery = new { mode = "Automatic" }, monitoringEnabled = true }      // Automatic без цели
        };

        foreach (var body in bodies)
        {
            using var response = await PutMonitoringAsync(client, deployment.Id, 1, JsonContent.Create(body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task Automatic_requires_an_approved_target_of_this_deployment()
    {
        using var client = Client();
        var deployment = await PersistenceTests.CreateDeploymentAsync(client);
        var other = await PersistenceTests.CreateDeploymentAsync(client);
        var foreignTarget = await PersistenceTests.CreateTargetAsync(client, other.Id);

        foreach (var targetId in new[] { Guid.NewGuid(), foreignTarget.Id })
        {
            using var response = await PutMonitoringAsync(client, deployment.Id, 1,
                JsonContent.Create(Monitoring(mode: RecoveryMode.Automatic, targetId: targetId)));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_recovery_target", problem.GetProperty("code").GetString());
        }
    }

    [DatabaseFact]
    public async Task Concurrent_updates_with_one_etag_allow_only_one_winner()
    {
        using var client = Client();
        var deployment = await PersistenceTests.CreateDeploymentAsync(client);

        var responses = await Task.WhenAll(
            PutMonitoringAsync(client, deployment.Id, 1, JsonContent.Create(Monitoring(interval: 20))),
            PutMonitoringAsync(client, deployment.Id, 1, JsonContent.Create(Monitoring(interval: 30))));

        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.PreconditionFailed },
            responses.Select(x => x.StatusCode).OrderBy(x => (int)x).ToArray());
        foreach (var response in responses) response.Dispose();
    }

    [DatabaseFact]
    public async Task Switching_to_automatic_during_an_open_incident_creates_one_job()
    {
        using var client = Client();
        var deployment = await PersistenceTests.CreateDeploymentAsync(client);
        var target = await PersistenceTests.CreateTargetAsync(client, deployment.Id);
        var incidentId = await OpenIncidentAsync(deployment.Id);
        var command = AutomaticCommand(deployment, target.Id, incidentId);

        await Assert.ThrowsAsync<OperationRejectedException>(() => SubmitAutomaticAsync(command));

        using var switched = await PutMonitoringAsync(client, deployment.Id, 1,
            JsonContent.Create(Monitoring(mode: RecoveryMode.Automatic, targetId: target.Id)));
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);

        var created = await SubmitAutomaticAsync(command);
        Assert.True(created.Created);
        var replayed = await SubmitAutomaticAsync(command);
        Assert.False(replayed.Created);
        Assert.Equal(created.Job.Id, replayed.Job.Id);

        await using var db = fixture.CreateDb();
        Assert.Equal(1, await db.Jobs.CountAsync(x => x.DeploymentId == deployment.Id));
    }

    [DatabaseFact]
    public async Task Policy_and_threshold_changes_keep_the_open_incident_visible_but_a_new_path_hides_it()
    {
        using var client = Client();
        var deployment = await PersistenceTests.CreateDeploymentAsync(client);
        var incidentId = await OpenIncidentAsync(deployment.Id);

        using var tuned = await PutMonitoringAsync(client, deployment.Id, 1,
            JsonContent.Create(Monitoring(interval: 30, failureThreshold: 5, recoveryThreshold: 4)));
        Assert.Equal(HttpStatusCode.OK, tuned.StatusCode);
        var health = await ReadHealthAsync(client, deployment.Id);
        Assert.Equal(DeploymentHealthStatus.Unhealthy, health.Status);
        Assert.Equal((Guid?)incidentId, health.IncidentId);

        using var moved = await PutMonitoringAsync(client, deployment.Id, 2,
            JsonContent.Create(Monitoring(path: "/ready", interval: 30, failureThreshold: 5, recoveryThreshold: 4)));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        health = await ReadHealthAsync(client, deployment.Id);
        Assert.Equal(DeploymentHealthStatus.Unknown, health.Status);
        Assert.Null(health.IncidentId);

        await using var db = fixture.CreateDb();
        Assert.Equal(1, await db.Incidents.CountAsync(x => x.DeploymentId == deployment.Id && x.ResolvedAt == null));
    }

    [DatabaseFact]
    public async Task Path_change_makes_the_observation_stale_for_automatic_recovery()
    {
        using var client = Client();
        var deployment = await PersistenceTests.CreateDeploymentAsync(client);
        var target = await PersistenceTests.CreateTargetAsync(client, deployment.Id);
        var incidentId = await OpenIncidentAsync(deployment.Id);
        var command = AutomaticCommand(deployment, target.Id, incidentId);

        using var response = await PutMonitoringAsync(client, deployment.Id, 1,
            JsonContent.Create(Monitoring(path: "/ready", mode: RecoveryMode.Automatic, targetId: target.Id)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await Assert.ThrowsAsync<OperationRejectedException>(() => SubmitAutomaticAsync(command));

        await using (var db = fixture.CreateDb())
            await db.Health.Where(x => x.DeploymentId == deployment.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ConfigurationRevision, 2));
        Assert.True((await SubmitAutomaticAsync(command)).Created);
    }

    [DatabaseFact]
    public async Task Policy_change_racing_with_automatic_submission_never_creates_a_job_after_notify_only()
    {
        using var client = Client();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var deployment = await PersistenceTests.CreateDeploymentAsync(client);
            var target = await PersistenceTests.CreateTargetAsync(client, deployment.Id);
            var incidentId = await OpenIncidentAsync(deployment.Id);
            using var enable = await PutMonitoringAsync(client, deployment.Id, 1,
                JsonContent.Create(Monitoring(mode: RecoveryMode.Automatic, targetId: target.Id)));
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
            var command = AutomaticCommand(deployment, target.Id, incidentId);

            var update = PutMonitoringAsync(client, deployment.Id, 2, JsonContent.Create(Monitoring(mode: RecoveryMode.NotifyOnly)));
            var submit = TrySubmitAutomaticAsync(command);
            await Task.WhenAll(update, submit);
            using var updateResponse = await update;
            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

            await using var db = fixture.CreateDb();
            var stored = await db.Deployments.AsNoTracking().SingleAsync(x => x.Id == deployment.Id);
            Assert.Equal(Domain.RecoveryMode.NotifyOnly, stored.Definition.Recovery.Mode);
            var jobs = await db.Jobs.AsNoTracking().Where(x => x.DeploymentId == deployment.Id).ToListAsync();
            Assert.True(jobs.Count <= 1);
            Assert.All(jobs, job => Assert.True(job.CreatedAt <= stored.UpdatedAt));
        }
    }
}