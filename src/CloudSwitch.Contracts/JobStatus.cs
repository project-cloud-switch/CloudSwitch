using System.Text.Json.Serialization;

namespace CloudSwitch.Contracts;

/// <summary>Shared job states. Transition enforcement belongs to the owning service.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
public enum JobStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}
