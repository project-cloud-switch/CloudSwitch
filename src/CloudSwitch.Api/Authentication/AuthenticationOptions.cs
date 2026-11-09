using System.ComponentModel.DataAnnotations;

namespace CloudSwitch.Api.Authentication;

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    [Required]
    public string Authority { get; init; } = "";

    [Required]
    public string Audience { get; init; } = "cloudswitch-api";

    public bool RequireHttpsMetadata { get; init; } = true;
}
