using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace CloudSwitch.Api.Tests;

public sealed class RateLimitingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("application/json")]
    [InlineData("text/html")]
    public async Task User_budgets_are_shared_across_routes_and_token_refreshes(string accept)
    {
        using var app = WithLimits(authenticated: 2);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken());
        using var first = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await client.GetAsync("/api/v1/modules");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.CreateToken(expires: DateTime.UtcNow.AddMinutes(10)));
        using var rejected = await client.GetAsync("/api/v1/me");
        await AssertRateLimitedAsync(rejected);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken(subject: "another-user"));
        using var otherUser = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, otherUser.StatusCode);
    }

    [Fact]
    public async Task Anonymous_budgets_use_connection_ips_and_include_invalid_tokens()
    {
        using var app = WithLimits(anonymous: 1);
        var first = await app.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
            context.Request.Path = "/api/v1/me";
        });
        Assert.Equal(StatusCodes.Status401Unauthorized, first.Response.StatusCode);

        var rejected = await app.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
            context.Request.Path = "/api/v1/me";
            context.Request.Headers.Authorization = "Bearer not-a-valid-token";
            context.Request.Headers["X-Forwarded-For"] = "192.0.2.2";
        });
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);

        var otherIp = await app.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.2");
            context.Request.Path = "/api/v1/me";
        });
        Assert.Equal(StatusCodes.Status401Unauthorized, otherIp.Response.StatusCode);
    }

    [Fact]
    public async Task Submission_budget_spans_deployments_and_preserves_the_users_read_budget()
    {
        using var app = WithLimits(submissions: 1);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken());
        var input = new { targetConfigurationId = Guid.NewGuid() };
        using var first = await client.PostAsJsonAsync($"/api/v1/deployments/{Guid.NewGuid()}/jobs", input);
        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        using var rejected = await client.PostAsJsonAsync($"/api/v1/deployments/{Guid.NewGuid()}/jobs", input);
        await AssertRateLimitedAsync(rejected);
        using var read = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken(subject: "another-user"));
        using var otherUser = await client.PostAsJsonAsync($"/api/v1/deployments/{Guid.NewGuid()}/jobs", input);
        Assert.Equal(HttpStatusCode.BadRequest, otherUser.StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_probes_bypass_limits_and_do_not_consume_anonymous_budgets(string path)
    {
        using var limitedApp = WithLimits(anonymous: 1);
        using var app = limitedApp.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<HealthCheckServiceOptions>(options => options.Registrations.Clear())));
        using var client = app.CreateClient();
        using var firstProbe = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, firstProbe.StatusCode);
        using var firstApiRequest = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, firstApiRequest.StatusCode);
        using var rejected = await client.GetAsync("/api/v1/me");
        await AssertRateLimitedAsync(rejected);
        using var secondProbe = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, secondProbe.StatusCode);
    }

    private WebApplicationFactory<Program> WithLimits(int authenticated = 10, int anonymous = 10, int submissions = 10)
        => factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:AuthenticatedRequestsPerMinute"] = authenticated.ToString(),
                ["RateLimiting:AnonymousRequestsPerMinute"] = anonymous.ToString(),
                ["RateLimiting:JobSubmissionsPerMinute"] = submissions.ToString()
            })));

    private static async Task AssertRateLimitedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var retryAfter = Assert.IsType<RetryConditionHeaderValue>(response.Headers.RetryAfter);
        Assert.InRange(retryAfter.Delta.GetValueOrDefault().TotalSeconds, 1, 60);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.Equal("rate_limit_exceeded", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }
}
