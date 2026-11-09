using System.Text.Json.Serialization;

namespace CloudSwitch.Contracts;

/// <summary>Execution phases of a stateless container migration job.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationPhase>))]
public enum MigrationPhase
{
    Pending = 0,
    Provisioning = 1,
    TargetValidation = 2,
    TrafficCutover = 3,
    EntrypointVerification = 4,
    Completed = 5
}
