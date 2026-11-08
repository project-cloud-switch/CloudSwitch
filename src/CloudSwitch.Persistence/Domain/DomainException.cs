using System.Diagnostics.CodeAnalysis;

namespace CloudSwitch.Persistence.Domain;

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal static class DomainGuard
{
    public static void Require([DoesNotReturnIf(false)] bool valid, string message)
    {
        if (!valid)
            throw new DomainException("invalid_domain_state", message);
    }

    public static void Identity(Guid id, string name)
        => Require(id != Guid.Empty, $"{name} must be a nonempty UUID.");

    public static string Text(string? value, string name, int maximumLength = int.MaxValue)
    {
        Require(!string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength,
            $"{name} is required and must not exceed {maximumLength} characters.");
        return value;
    }

    public static Uri Endpoint(Uri endpoint)
    {
        Require(endpoint is { IsAbsoluteUri: true, Scheme: "http" or "https" }
            && string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query)
            && string.IsNullOrEmpty(endpoint.Fragment), "Use an absolute HTTP(S) endpoint without credentials, query or fragment.");
        return endpoint;
    }
}
