using System.Text.RegularExpressions;

namespace CloudSwitch.Worker.Infrastructure;

/// <summary>
/// Sanitizes command output and error logs so credentials and secrets never enter events,
/// database records, or log sinks.
/// </summary>
public static partial class OutputSanitizer
{
    public const string RedactedMask = "***REDACTED***";

    [GeneratedRegex(@"(?i)([""']?[a-zA-Z0-9_\-]*(?:password|secret|client[_-]?secret|token|bearer|api[_-]?key|access[_-]?key|credentials?)[a-zA-Z0-9_\-]*[""']?)(\s*[:=]\s*[""']?)([^""'\s,;]+)([""']?)")]
    private static partial Regex SensitivePatternRegex();

    [GeneratedRegex(@"(?i)bearer\s+[a-zA-Z0-9\._\-]+")]
    private static partial Regex BearerTokenRegex();

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

        sanitized = SensitivePatternRegex().Replace(sanitized, $"$1$2{RedactedMask}$4");
        sanitized = BearerTokenRegex().Replace(sanitized, $"Bearer {RedactedMask}");

        if (sanitized.Length > maxLength)
        {
            sanitized = string.Concat(sanitized.AsSpan(0, maxLength), "... [TRUNCATED]");
        }

        return sanitized;
    }
}
