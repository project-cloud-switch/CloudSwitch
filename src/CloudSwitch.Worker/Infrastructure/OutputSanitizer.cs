using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CloudSwitch.Worker.Infrastructure;

public static partial class OutputSanitizer
{
    public const string RedactedMask = "***REDACTED***";
    public const string Withheld = "[output withheld: it could not be sanitized safely]";
    private const int TimeoutMs = 200;

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|\z)",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]*", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex JsonWebToken();

    [GeneratedRegex(@"\b(Proxy-Authorization|Authorization)\b\s*[:=]\s*[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=\-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\bBasic\s+[A-Za-z0-9+/=]{16,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex BasicCredentials();

    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9+.\-]*://)[^/\s@]*@", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UrlUserInfo();

    [GeneratedRegex(@"\bvault://\S*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex VaultReference();

    [GeneratedRegex(
        @"([""']?[\w\-.]*(?:password|passwd|pwd|secret|token|bearer|api[_-]?key|access[_-]?key|private[_-]?key|credentials?|cookie)[\w\-.]*[""']?\s*[:=]\s*)(""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;&""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex KeyValueSecret();

    public static string Sanitize(string? rawOutput, IEnumerable<string>? explicitSecrets = null, int maxLength = 4000)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return string.Empty;
        try
        {
            var text = rawOutput;

            foreach (var secret in (explicitSecrets ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length > 2)
                .Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length))
            {
                text = text.Replace(secret, RedactedMask, StringComparison.OrdinalIgnoreCase);
                foreach (var variant in EncodedVariants(secret))
                    text = text.Replace(variant, RedactedMask, StringComparison.Ordinal);
            }

            text = PrivateKeyBlock().Replace(text, RedactedMask);
            text = JsonWebToken().Replace(text, RedactedMask);
            text = AuthorizationHeader().Replace(text, "$1: " + RedactedMask);
            text = BearerToken().Replace(text, "Bearer " + RedactedMask);
            text = BasicCredentials().Replace(text, "Basic " + RedactedMask);
            text = UrlUserInfo().Replace(text, "$1" + RedactedMask + "@");
            text = VaultReference().Replace(text, "vault://" + RedactedMask);
            text = KeyValueSecret().Replace(text, "$1" + RedactedMask);

            text = ReplaceControlCharacters(text);
            return text.Length > maxLength ? string.Concat(text.AsSpan(0, maxLength), "... [TRUNCATED]") : text;
        }
        catch (RegexMatchTimeoutException)
        {
            return Withheld;
        }
    }

    private static IEnumerable<string> EncodedVariants(string secret)
    {
        var variants = new[]
        {
            Uri.EscapeDataString(secret),
            WebUtility.UrlEncode(secret),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(secret))
        };
        return variants.Where(variant => variant.Length > 2 && variant != secret).Distinct(StringComparer.Ordinal);
    }

    private static string ReplaceControlCharacters(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (char.IsControl(chars[i]))
                chars[i] = ' ';
        return new string(chars);
    }
}