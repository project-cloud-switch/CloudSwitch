using System.Text.Json.Serialization;

namespace CloudSwitch.Contracts;

public sealed record ApplicationConfiguration
{
    public string? ImagePullSecretReference { get; init; }
    public required string Image { get; init; }
    public required int Port { get; init; }
    public required int CpuMillicores { get; init; }
    public required int MemoryMegabytes { get; init; }
    public Dictionary<string, string> Settings { get; init; } = [];
    public Dictionary<string, string> SecretReferences { get; init; } = [];
}

public sealed record TrafficConfiguration
{
    public required string Driver { get; init; }
    public required string RecordName { get; init; }
    public string? ZoneReference { get; init; }
    public string? CredentialReference { get; init; }
}

public sealed record TargetConfigurationSnapshot
{
    public required Guid Id { get; init; }
    public required long Revision { get; init; }
    public required string Provider { get; init; }
    public required string ModuleId { get; init; }
    public required string ModuleSource { get; init; }
    public required string ModuleRevision { get; init; }
    public Dictionary<string, string> Inputs { get; init; } = [];
    public Dictionary<string, string> SecretReferences { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<CutoverOutcome>))]
public enum CutoverOutcome { NotStarted, Applied, Verified, Uncertain }
