using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudSwitch.Api.Contracts;
using CloudSwitch.Contracts;
using CloudSwitch.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Microsoft.EntityFrameworkCore; 
using Domain = CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Api.Tests;

[Collection("PostgreSQL")]
[Trait("Category", "ExternalIntegration")]
public sealed class ReferenceAccessIntegrationTests(DatabaseFixture fixture)
{
    private HttpClient Client(string subject, Action<AccessOptions>? zones = null)
    {
        var client = zones is null
            ? fixture.Factory.CreateClient()
            : fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(
                services => services.PostConfigure<AccessOptions>(zones))).CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.Factory.CreateToken(subject: subject));
        return client;
    }

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private static DeploymentRequest WithSecret(Guid userId, string reference)
    {
        var input = PersistenceTests.Definition(userId);
        return input with { Definition = input.Definition with { Application = input.Definition.Application with
            { SecretReferences = new() { ["API_KEY"] = reference } } } };
    }

    private static DeploymentRequest WithTraffic(Guid userId, string zone, string record, string credential)
    {
        var input = PersistenceTests.Definition(userId);
        return input with { Definition = input.Definition with { Traffic = new TrafficConfiguration
            { Driver = "cloudflare-dns", RecordName = record, ZoneReference = zone, CredentialReference = credential } } };
    }

    private static async Task AssertRejectedAsync(HttpResponseMessage response, string reference, string code)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(reference, body);
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    [DatabaseFact]
    public async Task Foreign_vault_references_are_rejected_on_registration_and_update_with_one_answer()
    {
        using var owner = Client(Unique("access-owner"));
        using var stranger = Client(Unique("access-stranger"));
        var ownerId = await PersistenceTests.UserIdAsync(owner);
        var strangerId = await PersistenceTests.UserIdAsync(stranger);

        foreach (var reference in new[]
        {
            PersistenceTests.Secret(strangerId, "app#key"),
            $"vault://users/{ownerId:D}x/app#key",
            $"vault://users/{ownerId.ToString("D").ToUpperInvariant()}/app#key",
            "vault://demo#key"
        })
        {
            using var response = await owner.PostAsJsonAsync("/api/v1/deployments", WithSecret(ownerId, reference));
            await AssertRejectedAsync(response, reference, "reference_not_permitted");
        }

        using var traversal = await owner.PostAsJsonAsync("/api/v1/deployments",
            WithSecret(ownerId, $"vault://users/{ownerId:D}/../{strangerId:D}/app#key"));
        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);

        var deployment = await PersistenceTests.CreateDeploymentAsync(owner);
        owner.DefaultRequestHeaders.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        var foreign = PersistenceTests.Secret(strangerId, "app#key");
        using var update = await owner.PutAsJsonAsync($"/api/v1/deployments/{deployment.Id}", WithSecret(ownerId, foreign));
        await AssertRejectedAsync(update, foreign, "reference_not_permitted");
    }

    [DatabaseFact]
    public async Task Foreign_registry_and_target_references_are_rejected()
    {
        using var owner = Client(Unique("access-owner"));
        using var stranger = Client(Unique("access-stranger"));
        var ownerId = await PersistenceTests.UserIdAsync(owner);
        var strangerId = await PersistenceTests.UserIdAsync(stranger);
        var foreign = PersistenceTests.Secret(strangerId, "registry#token");

        var input = PersistenceTests.Definition(ownerId);
        input = input with { Definition = input.Definition with { Application = input.Definition.Application with
            { ImagePullSecretReference = foreign } } };
        using var registry = await owner.PostAsJsonAsync("/api/v1/deployments", input);
        await AssertRejectedAsync(registry, foreign, "reference_not_permitted");

        var deployment = await PersistenceTests.CreateDeploymentAsync(owner);
        using var target = await owner.PostAsJsonAsync($"/api/v1/deployments/{deployment.Id}/targets", new TargetRequest
        {
            Provider = "local", ModuleId = "local-stateless-v1", Inputs = new() { ["region"] = "test" },
            SecretReferences = new() { ["cloud"] = foreign }
        });
        await AssertRejectedAsync(target, foreign, "reference_not_permitted");

        using var own = await owner.PostAsJsonAsync($"/api/v1/deployments/{deployment.Id}/targets", new TargetRequest
        {
            Provider = "local", ModuleId = "local-stateless-v1", Inputs = new() { ["region"] = "test" },
            SecretReferences = new() { ["cloud"] = PersistenceTests.Secret(ownerId, "cloud#key") }
        });
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
    }

    [DatabaseFact]
    public async Task Dns_zone_and_record_must_belong_to_the_user()
    {
        var subject = Unique("access-dns");
        using var plain = Client(subject);
        var userId = await PersistenceTests.UserIdAsync(plain);
        var credential = PersistenceTests.Secret(userId, "dns#token");
        using var owner = Client(subject, options => options.DnsZones = [new DnsZoneGrant { Zone = "example.test", UserIds = [userId] }]);

        using var allowed = await owner.PostAsJsonAsync("/api/v1/deployments",
            WithTraffic(userId, "example.test", "app.example.test", credential));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);

        foreach (var (zone, record) in new[]
        {
            ("other.test", "app.other.test"),
            ("example.test", "app.other.test"),
            ("example.test", "evilexample.test"),
            ("*.example.test", "app.example.test")
        })
        {
            using var response = await owner.PostAsJsonAsync("/api/v1/deployments", WithTraffic(userId, zone, record, credential));
            await AssertRejectedAsync(response, zone, "reference_not_permitted");
        }

        using var withoutGrant = Client(subject);
        using var denied = await withoutGrant.PostAsJsonAsync("/api/v1/deployments",
            WithTraffic(userId, "example.test", "app.example.test", credential));
        await AssertRejectedAsync(denied, "example.test", "reference_not_permitted");
    }

    [DatabaseFact]
    public async Task Revoked_dns_zone_blocks_new_jobs_but_a_replay_still_returns_the_original_job()
    {
        var subject = Unique("access-revoke");
        using var plain = Client(subject);
        var userId = await PersistenceTests.UserIdAsync(plain);
        using var granted = Client(subject, options => options.DnsZones = [new DnsZoneGrant { Zone = "example.test", UserIds = [userId] }]);

        using var created = await granted.PostAsJsonAsync("/api/v1/deployments",
            WithTraffic(userId, "example.test", "app.example.test", PersistenceTests.Secret(userId, "dns#token")));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var deployment = (await created.Content.ReadFromJsonAsync<DeploymentResponse>())!;
        var target = await PersistenceTests.CreateTargetAsync(granted, deployment.Id);

        using var first = await PersistenceTests.SubmitAsync(granted, deployment.Id, target.Id, "granted-key");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var job = (await first.Content.ReadFromJsonAsync<JobResponse>())!;
        await using (var db = fixture.CreateDb())
            await db.Jobs.Where(x => x.Id == job.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, Domain.JobStatus.Failed));

        using var revoked = Client(subject);
        using var denied = await PersistenceTests.SubmitAsync(revoked, deployment.Id, target.Id, "after-revoke");
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var body = await denied.Content.ReadAsStringAsync();
        Assert.Equal("reference_access_denied", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("example.test", body);

        using var replay = await PersistenceTests.SubmitAsync(revoked, deployment.Id, target.Id, "granted-key");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(job.Id, (await replay.Content.ReadFromJsonAsync<JobResponse>())!.Id);
    }
}