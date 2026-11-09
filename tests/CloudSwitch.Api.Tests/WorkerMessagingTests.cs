using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CloudSwitch.Api.Contracts;
using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using CloudSwitch.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CloudSwitch.Api.Tests;

public sealed class MessagingFactAttribute : FactAttribute
{
    public MessagingFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLOUDSWITCH_TEST_POSTGRES"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLOUDSWITCH_TEST_RABBITMQ")))
            Skip = "Set CLOUDSWITCH_TEST_POSTGRES and CLOUDSWITCH_TEST_RABBITMQ to run isolated broker integration.";
    }
}

[Collection("PostgreSQL")]
public sealed class WorkerMessagingTests
{
    [MessagingFact]
    public async Task Api_outbox_delivers_to_worker_and_worker_commits_verified_progress()
    {
        var database = new DatabaseFixture();
        try
        {
            await database.InitializeAsync();
            await VerifyMessagingAsync(database);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static async Task VerifyMessagingAsync(DatabaseFixture fixture)
    {
        var broker = new Uri(Environment.GetEnvironmentVariable("CLOUDSWITCH_TEST_RABBITMQ")!);
        var credentials = broker.UserInfo.Split(':', 2).Select(Uri.UnescapeDataString).ToArray();
        var virtualHost = "cloudswitch_test_" + Guid.NewGuid().ToString("N");
        using var management = new HttpClient
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("CLOUDSWITCH_TEST_RABBITMQ_MANAGEMENT") ?? "http://localhost:15672")
        };
        management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials[0]}:{credentials[1]}")));
        using var created = await management.PutAsJsonAsync($"/api/vhosts/{virtualHost}", new { });
        created.EnsureSuccessStatusCode();
        try
        {
            using var permission = await management.PutAsJsonAsync($"/api/permissions/{virtualHost}/{Uri.EscapeDataString(credentials[0])}",
                new { configure = ".*", write = ".*", read = ".*" });
            permission.EnsureSuccessStatusCode();
            var configuration = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = fixture.ConnectionString,
                ["RabbitMq:Host"] = broker.Host, ["RabbitMq:Port"] = broker.Port.ToString(),
                ["RabbitMq:VirtualHost"] = virtualHost, ["RabbitMq:Username"] = credentials[0], ["RabbitMq:Password"] = credentials[1],
                ["Worker:Simulation"] = "true", ["Logging:LogLevel:Default"] = "Critical"
            };
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(configuration);
            builder.Services.AddCloudSwitchWorker(builder.Configuration, builder.Environment);
            using var worker = builder.Build();
            await worker.StartAsync();
            try
            {
                var progress = new ConcurrentQueue<JobStatusChanged>();
                var observer = worker.Services.GetRequiredService<IReceiveEndpointConnector>()
                    .ConnectReceiveEndpoint("cloudswitch-test-progress", (_, endpoint) => endpoint.Handler<JobStatusChanged>(context =>
                    {
                        progress.Enqueue(context.Message);
                        return Task.CompletedTask;
                    }));
                await observer.Ready;
                using var api = fixture.Factory.WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, settings) =>
                    settings.AddInMemoryCollection(configuration)));
                using var client = api.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Factory.CreateToken());
                var deployment = await PersistenceTests.CreateDeploymentAsync(client);
                var target = await PersistenceTests.CreateTargetAsync(client, deployment.Id);
                using var submitted = await PersistenceTests.SubmitAsync(client, deployment.Id, target.Id, "broker-integration");
                submitted.EnsureSuccessStatusCode();
                var job = (await submitted.Content.ReadFromJsonAsync<JobResponse>())!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                while (job.Status is JobStatus.Pending or JobStatus.Running)
                {
                    await Task.Delay(200, timeout.Token);
                    job = (await client.GetFromJsonAsync<JobResponse>($"/api/v1/jobs/{job.Id}", timeout.Token))!;
                }
                Assert.Equal(JobStatus.Succeeded, job.Status);
                Assert.Equal(MigrationPhase.Completed, job.Phase);
                Assert.Equal(CutoverOutcome.Verified, job.Cutover);
                Assert.Equal(5, job.Sequence);
                while (progress.Count(item => item.JobId == job.Id) < 5)
                    await Task.Delay(50, timeout.Token);
                var delivered = Assert.Single(progress, item => item.JobId == job.Id && item.Status == JobStatus.Succeeded);
                Assert.Equal(job.CorrelationId, delivered.CorrelationId);
                Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, progress.Where(item => item.JobId == job.Id)
                    .Select(item => item.Sequence).Order());
            }
            finally
            {
                await worker.StopAsync();
            }
        }
        finally
        {
            using var deleted = await management.DeleteAsync($"/api/vhosts/{virtualHost}");
            deleted.EnsureSuccessStatusCode();
        }
    }
}
