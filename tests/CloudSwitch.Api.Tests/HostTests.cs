using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudSwitch.Api.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CloudSwitch.Api.Tests;

public sealed class HostTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("application/json")]
    [InlineData("text/html")]
    [InlineData("application/xml")]
    public async Task Application_errors_preserve_their_status_and_problem_format_for_any_accept_header(string accept)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken());
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        using var response = await client.GetAsync("/api/v1/jobs?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_pagination", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/html")]
    public async Task Unexpected_errors_return_sanitized_problem_details(string accept)
    {
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<ICurrentAccount>(_ => new FailingCurrentAccount(
                new InvalidOperationException("private diagnostic details")))));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken());
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        using var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private diagnostic details", body);
        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("internal_error", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Database_outages_return_service_unavailable()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken());
        using var response = await client.GetAsync("/api/v1/deployments");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("database_unavailable", problem.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrapped_database_write_failures_return_service_unavailable(bool wrapped)
    {
        Exception failure = new DbUpdateException("save failed", new NpgsqlException("connection failed", new TimeoutException()));
        if (wrapped)
            failure = new InvalidOperationException("transient failure", failure);
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<ICurrentAccount>(_ => new FailingCurrentAccount(failure))));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken());
        using var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("database_unavailable", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Liveness_does_not_require_authentication_or_external_dependencies()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("postgres", HealthStatus.Unhealthy, HttpStatusCode.ServiceUnavailable)]
    [InlineData("broker", HealthStatus.Degraded, HttpStatusCode.ServiceUnavailable)]
    [InlineData("all", HealthStatus.Healthy, HttpStatusCode.OK)]
    public async Task Readiness_checks_database_and_messaging(string dependency, HealthStatus status, HttpStatusCode expected)
    {
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<HealthCheckServiceOptions>(options =>
            {
                // Preserve the production checks' names/tags while replacing network operations.
                Assert.Equal(2, options.Registrations.Count);
                Assert.All(options.Registrations, check => Assert.Contains("ready", check.Tags));
                foreach (var check in options.Registrations)
                    check.Factory = _ => new FixedHealthCheck(
                        (dependency == "postgres" && check.Name == "postgres")
                        || (dependency == "broker" && check.Name != "postgres") ? status : HealthStatus.Healthy);
            })));
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Null_request_values_return_validation_problems()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken());
        foreach (var body in new[]
        {
            "null",
            """{"name": null, "definition": null}""",
            """{"name": "demo", "definition": {"environment": "dev", "sourceProvider": "yandex", "sourceEndpoint": "https://app.example.test", "application": null, "healthCheck": null, "traffic": null, "recovery": null}}"""
        })
        {
            using var response = await client.PostAsJsonAsync("/api/v1/deployments", JsonNode.Parse(body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.NotEmpty(problem.GetProperty("errors").EnumerateObject());
            Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        }
    }

    [Fact]
    public async Task Openapi_documents_bearer_authentication_on_identity_endpoint()
    {
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        Assert.Equal("bearer", root.GetProperty("components").GetProperty("securitySchemes")
            .GetProperty("Bearer").GetProperty("scheme").GetString());
        Assert.True(root.GetProperty("paths").GetProperty("/api/v1/me").GetProperty("get")
            .GetProperty("security")[0].TryGetProperty("Bearer", out _));
        Assert.True(root.GetProperty("paths").GetProperty("/api/v1/me").GetProperty("get")
            .GetProperty("responses").TryGetProperty("429", out _));
    }

    [Fact]
    public async Task Openapi_documents_request_headers_and_problem_responses()
    {
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var path in new[] { "/api/v1/deployments/{id}", "/api/v1/deployments/{deploymentId}/targets/{id}" })
        foreach (var method in new[] { "put", "delete" })
        {
            var operation = paths.GetProperty(path).GetProperty(method);
            var header = Assert.Single(operation.GetProperty("parameters").EnumerateArray(), parameter =>
                parameter.GetProperty("in").GetString() == "header" && parameter.GetProperty("name").GetString() == "If-Match");
            Assert.True(header.GetProperty("required").GetBoolean());
            foreach (var status in new[] { "401", "403", "404", "409", "412", "428", "429", "500", "503" })
                Assert.True(operation.GetProperty("responses").GetProperty(status).GetProperty("content")
                    .TryGetProperty("application/problem+json", out _));
        }
        var submit = paths.GetProperty("/api/v1/deployments/{deploymentId}/jobs").GetProperty("post");
        var key = Assert.Single(submit.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.True(key.GetProperty("required").GetBoolean());
        var correlation = Assert.Single(submit.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "X-Correlation-ID");
        Assert.False(correlation.TryGetProperty("required", out var required) && required.GetBoolean());
    }

    [Fact]
    public async Task Multiple_idempotency_headers_are_rejected()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken());
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/deployments/{Guid.NewGuid()}/jobs")
        {
            Content = JsonContent.Create(new { targetConfigurationId = Guid.NewGuid() })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", new[] { "first", "second" });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency_key_required", problem.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("Authentication:Authority", "")]
    [InlineData("Authentication:Audience", "")]
    [InlineData("Authentication:Authority", "not-a-url")]
    [InlineData("RabbitMq:Port", "0")]
    [InlineData("ConnectionStrings:Postgres", "")]
    [InlineData("ConnectionStrings:Postgres", "not-a-connection-string")]
    [InlineData("RateLimiting:AuthenticatedRequestsPerMinute", "0")]
    [InlineData("RateLimiting:AnonymousRequestsPerMinute", "-1")]
    [InlineData("RateLimiting:JobSubmissionsPerMinute", "0")]
    public void Invalid_configuration_fails_startup(string key, string value)
    {
        using var app = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        Assert.Throws<OptionsValidationException>(() => app.CreateClient());
    }

    [Fact]
    public void Insecure_identity_metadata_is_rejected_outside_development()
    {
        using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Authority"] = "http://identity.example.test/realms/cloudswitch",
                    ["Authentication:RequireHttpsMetadata"] = "false",
                    ["ConnectionStrings:Postgres"] = "Host=localhost;Database=test;Username=test",
                    ["RabbitMq:Username"] = "test",
                    ["RabbitMq:Password"] = "test"
                }));
        });
        Assert.Throws<OptionsValidationException>(() => app.CreateClient());
    }

    private sealed class FixedHealthCheck(HealthStatus status) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new HealthCheckResult(status));
    }

    private sealed class FailingCurrentAccount(Exception exception) : ICurrentAccount
    {
        public Task<Account> GetAsync(CancellationToken ct)
            => throw exception;
    }
}
