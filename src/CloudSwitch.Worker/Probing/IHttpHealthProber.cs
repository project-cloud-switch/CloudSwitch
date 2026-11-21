namespace CloudSwitch.Worker.Probing;

public enum ProbeStatusCategory
{
    Healthy,
    HttpFailure,
    Timeout,
    ConnectionFailure,
    DnsFailure,
    SecurityPolicyViolation,
    Cancelled
}

public sealed record ProbeResult
{
    public required bool IsHealthy { get; init; }
    public required ProbeStatusCategory Category { get; init; }
    public int StatusCode { get; init; }
    public TimeSpan Latency { get; init; }
    public string? ObservedRevision { get; init; }
    public string? ResponseSnippet { get; init; }
    public string? ErrorMessage { get; init; }

    public static ProbeResult Success(int statusCode, TimeSpan latency, string? revision = null, string? snippet = null) => new()
    {
        IsHealthy = true,
        Category = ProbeStatusCategory.Healthy,
        StatusCode = statusCode,
        Latency = latency,
        ObservedRevision = revision,
        ResponseSnippet = snippet
    };

    public static ProbeResult Failure(ProbeStatusCategory category, string errorMessage, int statusCode = 0, TimeSpan latency = default) => new()
    {
        IsHealthy = false,
        Category = category,
        StatusCode = statusCode,
        Latency = latency,
        ErrorMessage = errorMessage
    };
}

public interface IHttpHealthProber
{
    Task<ProbeResult> ProbeAsync(
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? expectedRevision = null);
}
