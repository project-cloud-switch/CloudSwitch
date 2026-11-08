using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace CloudSwitch.Persistence.Domain;

public sealed record ApplicationConfiguration
{
    public string Image { get; }
    public int Port { get; }
    public int CpuMillicores { get; }
    public int MemoryMegabytes { get; }
    public IReadOnlyDictionary<string, string> Settings { get; }
    public IReadOnlyDictionary<string, string> SecretReferences { get; }

    public ApplicationConfiguration(string image, int port, int cpuMillicores, int memoryMegabytes,
        IReadOnlyDictionary<string, string>? settings = null, IReadOnlyDictionary<string, string>? secretReferences = null)
    {
        DomainGuard.Require(ConfigurationRules.PinnedImage(image), "Pin the application image to a SHA-256 digest.");
        DomainGuard.Require(port is >= 1 and <= 65535, "Application port must be between 1 and 65535.");
        DomainGuard.Require(cpuMillicores is >= 100 and <= 64000, "CPU must be between 100 and 64000 millicores.");
        DomainGuard.Require(memoryMegabytes is >= 64 and <= 262144, "Memory must be between 64 and 262144 MB.");
        Image = image;
        Port = port;
        CpuMillicores = cpuMillicores;
        MemoryMegabytes = memoryMegabytes;
        Settings = ConfigurationRules.Values(settings, 8192);
        SecretReferences = ConfigurationRules.Secrets(secretReferences);
        DomainGuard.Require(!Settings.Keys.Intersect(SecretReferences.Keys).Any(), "A setting cannot also be a secret reference.");
    }
}

public sealed record HealthCheckPolicy
{
    public string Path { get; }
    public int IntervalSeconds { get; }
    public int TimeoutSeconds { get; }
    public int FailureThreshold { get; }
    public int RecoveryThreshold { get; }

    public HealthCheckPolicy(string path, int intervalSeconds, int timeoutSeconds, int failureThreshold, int recoveryThreshold)
    {
        DomainGuard.Require(path is { Length: > 0 and <= 512 } && path.StartsWith('/') && !path.StartsWith("//")
            && !path.Contains('\\') && !path.Contains('#') && !path.Contains('?'), "Health checks require a local application path.");
        DomainGuard.Require(intervalSeconds is >= 1 and <= 3600 && timeoutSeconds is >= 1 and <= 60
            && timeoutSeconds <= intervalSeconds, "Health check timeout and interval are invalid.");
        DomainGuard.Require(failureThreshold is >= 1 and <= 100 && recoveryThreshold is >= 1 and <= 100,
            "Health check thresholds must be between 1 and 100.");
        Path = path;
        IntervalSeconds = intervalSeconds;
        TimeoutSeconds = timeoutSeconds;
        FailureThreshold = failureThreshold;
        RecoveryThreshold = recoveryThreshold;
    }
}

public sealed record RecoveryPolicy
{
    public RecoveryMode Mode { get; }
    public Guid? TargetConfigurationId { get; }

    public RecoveryPolicy(RecoveryMode mode = RecoveryMode.NotifyOnly, Guid? targetConfigurationId = null)
    {
        DomainGuard.Require(Enum.IsDefined(mode), "Recovery mode is invalid.");
        DomainGuard.Require(mode != RecoveryMode.Automatic || targetConfigurationId is { } id && id != Guid.Empty,
            "Automatic recovery requires a target configuration.");
        Mode = mode;
        TargetConfigurationId = targetConfigurationId;
    }
}

public sealed record TrafficConfiguration
{
    public string Driver { get; }
    public string RecordName { get; }
    public string? ZoneReference { get; }
    public string? CredentialReference { get; }

    public TrafficConfiguration(string driver, string recordName, string? zoneReference = null, string? credentialReference = null)
    {
        DomainGuard.Require(driver is "simulation" or "cloudflare-dns", "Traffic driver is invalid.");
        DomainGuard.Require(recordName is { Length: <= 253 } && Uri.CheckHostName(recordName) == UriHostNameType.Dns,
            "Traffic control requires a DNS entrypoint name.");
        DomainGuard.Require(driver != "cloudflare-dns" || !string.IsNullOrWhiteSpace(zoneReference)
            && ConfigurationRules.SecretReference(credentialReference), "DNS traffic control requires a zone and credential reference.");
        Driver = driver;
        RecordName = recordName;
        ZoneReference = zoneReference;
        CredentialReference = credentialReference;
    }
}

internal static partial class ConfigurationRules
{
    public static bool PinnedImage(string? value) => ImagePattern().IsMatch(value ?? "");
    public static bool SecretReference(string? value) => SecretPattern().IsMatch(value ?? "");

    public static IReadOnlyDictionary<string, string> Values(IReadOnlyDictionary<string, string>? values, int maximumLength)
    {
        values ??= FrozenDictionary<string, string>.Empty;
        DomainGuard.Require(values.Count <= 100 && values.All(pair => IdentifierPattern().IsMatch(pair.Key)
            && pair.Value is not null && pair.Value.Length <= maximumLength), "Configuration names or values are invalid.");
        return values.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static IReadOnlyDictionary<string, string> Secrets(IReadOnlyDictionary<string, string>? values)
    {
        var references = Values(values, 512);
        DomainGuard.Require(references.Values.All(SecretReference), "Credentials must use secret:// references.");
        return references;
    }

    [GeneratedRegex(@"\A[a-zA-Z_][a-zA-Z0-9_.-]{0,63}\z")]
    private static partial Regex IdentifierPattern();
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9._:/-]*@sha256:[a-fA-F0-9]{64}\z")]
    private static partial Regex ImagePattern();
    [GeneratedRegex(@"\Asecret://[a-zA-Z0-9][a-zA-Z0-9_./-]{0,500}\z")]
    private static partial Regex SecretPattern();
}
