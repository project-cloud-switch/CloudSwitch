using System.Diagnostics.CodeAnalysis;

namespace CloudSwitch.Persistence.Domain;

public static class DnsNames
{
    /// <summary>ASCII-only canonical form: lowercase, no trailing dot, at least two labels, no wildcards.</summary>
    public static bool TryCanonicalize(string? value, [NotNullWhen(true)] out string? name)
    {
        name = null;
        // ASCII is checked before lowercasing: ToLowerInvariant turns U+212A (Kelvin sign) into ASCII "k".
        if (string.IsNullOrEmpty(value) || value.Length > 254 || !value.All(char.IsAscii))
            return false;
        var candidate = value.EndsWith('.') ? value[..^1] : value;
        if (candidate.Length is 0 or > 253)
            return false;
        var labels = candidate.ToLowerInvariant().Split('.');
        if (labels.Length < 2 || !labels.All(ValidLabel))
            return false;
        name = string.Join('.', labels);
        return true;
    }

    /// <summary>Label-wise check: "evilexample.test" is not inside "example.test".</summary>
    public static bool IsInZone(string canonicalRecord, string canonicalZone)
        => canonicalRecord == canonicalZone || canonicalRecord.EndsWith("." + canonicalZone, StringComparison.Ordinal);

    private static bool ValidLabel(string label)
        => label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-'
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}