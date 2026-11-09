using System.Text.Json.Serialization;

namespace CloudSwitch.Contracts;

/// <summary>Identifies whether a migration job was requested manually or automatically by Doctor.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeploymentTrigger>))]
public enum DeploymentTrigger
{
    Manual = 0,
    Automatic = 1
}
