using System.ComponentModel.DataAnnotations;

namespace CloudSwitch.Api.Infrastructure;

public sealed class ApiRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    [Range(1, int.MaxValue)]
    public int AuthenticatedRequestsPerMinute { get; init; } = 120;

    [Range(1, int.MaxValue)]
    public int AnonymousRequestsPerMinute { get; init; } = 30;

    [Range(1, int.MaxValue)]
    public int JobSubmissionsPerMinute { get; init; } = 5;
}
