using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace CloudSwitch.Persistence;

public sealed record ApprovedModule
{
    [Required]
    public required string Id { get; init; }
    [Required]
    public required string Provider { get; init; }
    [Required]
    public required string Source { get; init; }
    [Required]
    public required string Revision { get; init; }
    [Required]
    public string[] AllowedInputs { get; init; } = [];

    /// <summary>Input names the caller must always supply. Empty means there are no mandatory inputs.</summary>
    public string[] RequiredInputs { get; init; } = [];

    /// <summary>Optional value type per input. Supported values: string, integer, number, boolean.</summary>
    public Dictionary<string, string> InputTypes { get; init; } = [];

    public string? ValidateInputs(IReadOnlyDictionary<string, string>? inputs)
    {
        inputs ??= new Dictionary<string, string>();
        var missing = RequiredInputs
            .Where(name => !inputs.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (missing.Length > 0)
            return $"Missing required inputs: {string.Join(", ", missing)}.";
        foreach (var (name, type) in InputTypes)
        {
            if (!inputs.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                continue;
            var valid = type.ToLowerInvariant() switch
            {
                "string" => true,
                "integer" => int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out _),
                "number" => double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _),
                "boolean" => bool.TryParse(value, out _),
                _ => false
            };
            if (!valid)
                return $"Input '{name}' must be of type {type}.";
        }
        return null;
    }
}

public sealed class ModuleCatalogOptions
{
    [Required]
    public List<ApprovedModule> Modules { get; init; } = [];
}

public sealed class ApprovedModules(IOptions<ModuleCatalogOptions> options)
{
    public IReadOnlyList<ApprovedModule> All => options.Value.Modules;
    public ApprovedModule? Find(string id, string provider) => All.SingleOrDefault(x => x.Id == id && x.Provider == provider);
}
