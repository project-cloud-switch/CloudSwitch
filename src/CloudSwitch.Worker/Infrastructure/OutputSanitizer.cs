using System.Text.RegularExpressions;

namespace CloudSwitch.Worker.Infrastructure;

/// <summary>
/// Sanitizes command output and error logs so credentials and secrets never enter events,
/// database records, or log sinks.
/// </summary>
public static partial class OutputSanitizer
{
    private static readonly Regex SensitivePatternRegex = new(
        @"(?i)(password|secret|token|bearer|api[_-]?key|access[_-]?key|credentials?)\s*[:=]\s*[""']?([^""'\s]+)[""']?",
        RegexOptions.Compiled);

    private static readonly Regex BearerTokenRegex = new(
        @"(?i)bearer\s+[a-zA-Z0-9\._\-]+",
        RegexOptions.Compiled);

    public const string RedactedMask = "***REDACTED***";

    public static string Sanitize(string? rawOutput, IEnumerable<string>? explicitSecrets = null, int maxLength = 4000)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return string.Empty;

        var sanitized = rawOutput;

        if (explicitSecrets != null)
        {
            foreach (var secret in explicitSecrets)
            {
                if (!string.IsNullOrWhiteSpace(secret) && secret.Length > 2)
                {
                    sanitized = sanitized.Replace(secret, RedactedMask, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        sanitized = SensitivePatternRegex.Replace(sanitized, m =>
            $"{m.Groups[1].Value}={RedactedMask}");

        sanitized = BearerTokenRegex.Replace(sanitized, $"Bearer {RedactedMask}");

        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized[..maxLength] + "... [TRUNCATED]";
        }

        return sanitized;
    }
}
