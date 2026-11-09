using System.ComponentModel.DataAnnotations;

namespace CloudSwitch.Worker;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    [Required]
    public string Host { get; init; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; init; } = 5672;

    [Required]
    public string VirtualHost { get; init; } = "/";

    [Required]
    public string Username { get; init; } = "";

    [Required]
    public string Password { get; init; } = "";
}
