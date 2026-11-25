using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudSwitch.Api.Contracts;
using CloudSwitch.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CloudSwitch.Api.Tests;

public sealed class SourceOptionsTests
{
    [Theory]
    [InlineData("b1g00000000000000000", true)]
    [InlineData("b1g0000000000000000", false)]
    [InlineData("B1G00000000000000000", false)]
    [InlineData("b1g00000000000000000\n", false)]
    [InlineData("b1g0000000000000000х", false)]
    [InlineData("", false)]
    public void Yandex_ids_have_one_format(string id, bool valid)
        => Assert.Equal(valid, new SourceOptions().Find("yandex")!.IsValidId(id));

    [Fact]
    public void Only_configured_source_providers_are_known()
    {
        var options = new SourceOptions();
        Assert.NotNull(options.Find("yandex"));
        Assert.Null(options.Find("aws"));
        Assert.Null(options.Find("YANDEX"));
    }
}

[Collection("PostgreSQL")]
[Trait("Category", "ExternalIntegration")]
public sealed class SourceIdentityTests(DatabaseFixture fixture)
{
    private HttpClient Client()
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.Factory.CreateToken(subject: "source-" + Guid.NewGuid().ToString("N")));
        return client;
    }

    [DatabaseFact]
    public async Task Unsupported_provider_and_invalid_source_identity_are_rejected()
    {
        using var client = Client();
        var valid = PersistenceTests.Definition(await PersistenceTests.UserIdAsync(client));
        var resource = valid.Definition.SourceResource;
        foreach (var definition in new[]
        {
            valid.Definition with { SourceProvider = "aws" },
            valid.Definition with { SourceResource = resource! with { Type = "gpu-cluster" } },
            valid.Definition with { SourceResource = resource with { Type = "" } },
            valid.Definition with { SourceResource = resource with { FolderId = "B1G" + new string('a', 17) } },
            valid.Definition with { SourceResource = resource with { FolderId = "short" } },
            valid.Definition with { SourceResource = resource with { ResourceId = resource.ResourceId + "\n" } },
            valid.Definition with { SourceResource = resource with { ResourceId = "" } }
        })
        {
            using var response = await client.PostAsJsonAsync("/api/v1/deployments", valid with { Definition = definition });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task Source_identity_is_frozen_in_the_job_and_needs_no_access_to_the_source()
    {
        using var client = Client();
        var deployment = await PersistenceTests.CreateDeploymentAsync(client);
        var target = await PersistenceTests.CreateTargetAsync(client, deployment.Id);

        using var response = await PersistenceTests.SubmitAsync(client, deployment.Id, target.Id, "source-snapshot");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<JobResponse>())!;

        await using var db = fixture.CreateDb();
        var stored = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        var source = stored.Request.Specification.Migration!.Source;
        Assert.Equal("yandex", source.Provider);
        Assert.Equal("compute-instance", source.Resource!.Type);
        Assert.Equal("b1g" + new string('a', 17), source.Resource.FolderId);
        Assert.Equal("fhm" + new string('b', 17), source.Resource.ResourceId);
    }

    [DatabaseFact]
    public async Task Monitoring_update_keeps_the_source_identity()
    {
        using var client = Client();
        var deployment = await PersistenceTests.CreateDeploymentAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/deployments/{deployment.Id}/monitoring")
        {
            Content = JsonContent.Create(new MonitoringUpdateRequest
            {
                HealthCheck = new CloudSwitch.Contracts.HealthCheckPolicy { Path = "/ready", IntervalSeconds = 10, TimeoutSeconds = 3, FailureThreshold = 3, RecoveryThreshold = 2 },
                Recovery = new CloudSwitch.Contracts.RecoveryPolicy(),
                MonitoringEnabled = true
            })
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = fixture.CreateDb();
        var stored = await db.Deployments.AsNoTracking().SingleAsync(x => x.Id == deployment.Id);
        Assert.Equal("fhm" + new string('b', 17), stored.Definition.SourceResource!.ResourceId);
    }
}