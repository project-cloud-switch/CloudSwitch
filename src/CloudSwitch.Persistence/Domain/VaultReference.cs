using System.Diagnostics.CodeAnalysis;

namespace CloudSwitch.Persistence.Domain;

/// <summary>A strictly parsed vault://path#key reference: exactly one textual form per secret.</summary>
public sealed class VaultReference
{
    private const string Scheme = "vault://";

    private VaultReference(IReadOnlyList<string> path, string key)
    {
        Path = path;
        Key = key;
    }

    public IReadOnlyList<string> Path { get; }
    public string Key { get; }
    public string Canonical => $"{Scheme}{string.Join('/', Path)}#{Key}";

    public static bool TryParse(string? value, [NotNullWhen(true)] out VaultReference? reference)
    {
        reference = null;
        if (value is null || value.Length > 512 || !value.StartsWith(Scheme, StringComparison.Ordinal))
            return false;
        var rest = value[Scheme.Length..];
        var hash = rest.IndexOf('#');
        if (hash < 1 || hash != rest.LastIndexOf('#'))
            return false;
        var segments = rest[..hash].Split('/');
        var key = rest[(hash + 1)..];
        if (segments.Length > 16 || !segments.All(ValidSegment) || !ValidKey(key))
            return false;
        reference = new VaultReference(segments, key);
        return true;
    }

    /// <summary>The only permitted namespace: users/{lowercase user id}/...</summary>
    public bool IsInUserNamespace(Guid userId)
        => Path.Count >= 2 && Path[0] == "users" && Path[1] == userId.ToString("D");

    // The first character must be a letter or digit: this rejects ".", "..", hidden and empty segments.
    private static bool ValidSegment(string segment)
        => segment.Length is >= 1 and <= 128 && char.IsAsciiLetterOrDigit(segment[0])
            && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    private static bool ValidKey(string key)
        => key.Length is >= 1 and <= 64 && key is not ("." or "..")
            && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
}