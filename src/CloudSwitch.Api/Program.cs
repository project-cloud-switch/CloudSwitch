using CloudSwitch.Api.Authentication;
using CloudSwitch.Api.Infrastructure;
using CloudSwitch.Api.OpenApi;
using CloudSwitch.Api.Services;
using CloudSwitch.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var migrateOnly = args.Contains("--migrate");
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--migrate").ToArray());

builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddControllers(options =>
{
    foreach (var status in new[] { 401, 403, 429, 500, 503 })
        options.Filters.Add(new ProducesResponseTypeAttribute(typeof(ProblemDetails), status, "application/problem+json"));
});
builder.Services.Configure<EndpointOptions>(builder.Configuration.GetSection("Endpoints"));
builder.Services.AddApiAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddApiRateLimiting(builder.Configuration);
builder.Services.AddApiInfrastructure(builder.Configuration);
builder.Services.AddScoped<DeploymentService>();
builder.Services.AddScoped<TargetService>();
builder.Services.AddScoped<JobService>();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecurityTransformer>();
    options.AddOperationTransformer<BearerSecurityTransformer>();
    options.AddOperationTransformer<ApiOperationTransformer>();
});

var app = builder.Build();

if (migrateOnly)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<CloudSwitchDbContext>();
    await db.Database.MigrateAsync();
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages(context => Results.Problem(statusCode: context.HttpContext.Response.StatusCode,
    extensions: new Dictionary<string, object?> { ["traceId"] = context.HttpContext.TraceIdentifier })
    .ExecuteAsync(context.HttpContext));
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous().DisableRateLimiting();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable
    }
}).AllowAnonymous().DisableRateLimiting();

app.MapControllers();

if (app.Environment.IsDevelopment())
    app.MapOpenApi().AllowAnonymous();

app.Run();
