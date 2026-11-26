using System.Text.RegularExpressions;

namespace CloudSwitch.Persistence;

public sealed class SourceOptions
{
    private static readonly List<SourceProviderRule> Defaults =
        [new()
        {
            Provider = "yandex",
            ResourceTypes = ["compute-instance", "serverless-container"],
            TargetProviders = ["local", "selectel", "docker", "yandex-fallback"]
        }];

    /// <summary>When configured under "Sources:Providers", replaces the defaults entirely.</summary>
    public List<SourceProviderRule>? Providers { get; set; }

    public SourceProviderRule? Find(string? provider)
        => (Providers ?? Defaults).FirstOrDefault(rule => string.Equals(rule.Provider, provider, StringComparison.Ordinal));
}

public sealed class SourceProviderRule
{
    public string Provider { get; set; } = "";
    public List<string> ResourceTypes { get; set; } = [];

    /// <summary>Providers this source may migrate to. Empty means the operator has not restricted the combination.</summary>
    public List<string> TargetProviders { get; set; } = [];

    /// <summary>Anchored pattern for folder and resource ids. Yandex Cloud ids are 20 lowercase alphanumerics.</summary>
    public string IdPattern { get; set; } = @"\A[a-z0-9]{20}\z";

    public bool IsValidId(string? value)
        => value is not null && value.Length <= 128
            && Regex.IsMatch(value, IdPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public bool AllowsTarget(string? targetProvider)
        => TargetProviders.Count == 0
            || targetProvider is not null && TargetProviders.Contains(targetProvider, StringComparer.Ordinal);
}