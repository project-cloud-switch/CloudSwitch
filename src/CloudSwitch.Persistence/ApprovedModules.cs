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
