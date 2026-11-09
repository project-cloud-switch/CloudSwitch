namespace CloudSwitch.Api.Services;

internal sealed class RequestValidationException(Dictionary<string, string[]> errors)
    : Exception("One or more validation errors occurred.")
{
    public Dictionary<string, string[]> Errors { get; } = errors;
}
