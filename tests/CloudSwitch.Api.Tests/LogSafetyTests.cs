using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudSwitch.Persistence;
using CloudSwitch.Persistence.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using Domain = CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Api.Tests;

[Collection("PostgreSQL")]
[Trait("Category", "ExternalIntegration")]
public sealed class LogSafetyTests(DatabaseFixture fixture)
{
    private const string Secret = "S3cr3t-Value-9f2c";

    [DatabaseFact]
    public async Task Failures_with_secrets_in_outer_inner_and_database_exceptions_never_reach_logs_or_responses()
    {
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.Factory.CreateToken(subject: "log-safety-" + Guid.NewGuid().ToString("N")));
        var deployment = await PersistenceTests.CreateDeploymentAsync(client);
        var target = await PersistenceTests.CreateTargetAsync(client, deployment.Id);

        var logs = new SafeCapturingLoggerProvider();
        using var app = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Information).AddProvider(logs));
            builder.ConfigureServices(services => services.AddDbContext<CloudSwitchDbContext>(
                options => options.AddInterceptors(new LeakyFailure())));
        });
        using var failing = app.CreateClient();
        failing.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/deployments/{deployment.Id}/jobs")
        { Content = JsonContent.Create(new Contracts.JobRequest(target.Id)) };
        request.Headers.Add("Idempotency-Key", "leaky-" + Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-Correlation-ID", Guid.NewGuid().ToString());
        using var response = await failing.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, body);
        Assert.DoesNotContain("db.internal", body);

        var all = string.Join('\n', logs.Lines);
        Assert.DoesNotContain(Secret, all);
        Assert.DoesNotContain("db.internal", all);
        Assert.DoesNotContain("admin:", all);

        var traceId = JsonDocument.Parse(body).RootElement.GetProperty("traceId").GetString()!;
        Assert.Contains(logs.Lines, line => line.Contains(traceId) && line.Contains("category=internal_error"));
    }

    [DatabaseFact]
    public async Task Sensitive_data_logging_is_disabled_and_the_safe_console_formatter_is_active()
    {
        await using var db = fixture.CreateDb();
        var core = db.GetService<IDbContextOptions>().FindExtension<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>();
        Assert.False(core?.IsSensitiveDataLoggingEnabled ?? false);
        Assert.False(core?.DetailedErrorsEnabled ?? false);

        var console = fixture.Factory.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue;
        Assert.Equal(SafeConsoleFormatter.Name, console.FormatterName);
    }

    private sealed class LeakyFailure : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Domain.Job>().Any(x => x.State == EntityState.Added))
                throw new InvalidOperationException(
                    $"storage failed: https://admin:{Secret}@db.internal/x?token={Secret}",
                    new AggregateException(
                        new Exception($"inner {Secret}"),
                        new PostgresException($"Key (IdempotencyKey)=({Secret})", "ERROR", "ERROR",
                            PostgresErrorCodes.UniqueViolation, detail: $"Key=({Secret})")));
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}