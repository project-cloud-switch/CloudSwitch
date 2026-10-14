using System.Text.Json.Serialization;

namespace CloudSwitch.Contracts;

/// <summary>Observed application availability, not proof of a provider-wide outage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeploymentHealthStatus>))]
public enum DeploymentHealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Unhealthy = 2
}
