using System.Text.Json.Serialization;

namespace CloudSwitch.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<RecoveryMode>))]
public enum RecoveryMode
{
    NotifyOnly = 0,
    Automatic = 1
}
