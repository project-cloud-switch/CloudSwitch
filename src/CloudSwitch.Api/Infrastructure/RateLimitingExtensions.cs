using System.Globalization;
using System.Threading.RateLimiting;
using CloudSwitch.Api.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace CloudSwitch.Api.Infrastructure;

public static class RateLimitingExtensions
{
    public const string JobSubmissionPolicy = "job-submission";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApiRateLimitOptions>()
            .Bind(configuration.GetSection(ApiRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddRateLimiter();
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<ApiRateLimitOptions>>((limiter, settings) =>
        {
            var limits = settings.Value;
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, (string Issuer, string Subject)>(context =>
            {
                var key = ClientKey(context);
                var permits = key.Issuer.Length == 0
                    ? limits.AnonymousRequestsPerMinute : limits.AuthenticatedRequestsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => Window(permits));
            });
            limiter.AddPolicy(JobSubmissionPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => Window(limits.JobSubmissionsPerMinute)));
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = RejectAsync;
        });
        return services;
    }

    private static (string Issuer, string Subject) ClientKey(HttpContext context)
        => context.RequestServices.GetRequiredService<ICurrentUser>().Identity is { } identity
            ? (identity.Issuer, identity.Subject)
            : ("", context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown");

    private static FixedWindowRateLimiterOptions Window(int permits)
        => new()
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        };

    private static async ValueTask RejectAsync(OnRejectedContext context, CancellationToken _)
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
        await Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "rate_limit_exceeded",
            detail: "Too many requests. Retry after the interval in the Retry-After header.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "rate_limit_exceeded", ["traceId"] = context.HttpContext.TraceIdentifier
            })
            .ExecuteAsync(context.HttpContext);
    }
}
