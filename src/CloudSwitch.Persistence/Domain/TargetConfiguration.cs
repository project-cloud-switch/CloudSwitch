namespace CloudSwitch.Persistence.Domain;

public record TargetDefinition
{
    public string Provider { get; }
    public string ModuleId { get; }
    public string ModuleSource { get; }
    public string ModuleRevision { get; }
    public IReadOnlyDictionary<string, string> Inputs { get; }
    public IReadOnlyDictionary<string, string> SecretReferences { get; }

    public TargetDefinition(string provider, string moduleId, string moduleSource, string moduleRevision,
        IReadOnlyDictionary<string, string>? inputs = null, IReadOnlyDictionary<string, string>? secretReferences = null)
    {
        Provider = DomainGuard.Text(provider, nameof(Provider));
        ModuleId = DomainGuard.Text(moduleId, nameof(ModuleId));
        ModuleSource = DomainGuard.Text(moduleSource, nameof(ModuleSource));
        ModuleRevision = DomainGuard.Text(moduleRevision, nameof(ModuleRevision));
        Inputs = ConfigurationRules.Values(inputs, 1024);
        SecretReferences = ConfigurationRules.Secrets(secretReferences);
    }
}

public sealed record TargetConfigurationSnapshot : TargetDefinition
{
    public Guid Id { get; }
    public long Revision { get; }

    public TargetConfigurationSnapshot(Guid id, long revision, string provider, string moduleId,
        string moduleSource, string moduleRevision, IReadOnlyDictionary<string, string>? inputs = null,
        IReadOnlyDictionary<string, string>? secretReferences = null)
        : base(provider, moduleId, moduleSource, moduleRevision, inputs, secretReferences)
    {
        DomainGuard.Identity(id, nameof(Id));
        DomainGuard.Require(revision > 0, "Target revision must be positive.");
        Id = id;
        Revision = revision;
    }
}

public sealed class TargetConfiguration
{
    public Guid Id { get; private set; }
    public Guid DeploymentId { get; private set; }
    public long Revision { get; private set; }
    public TargetConfigurationSnapshot Configuration { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private TargetConfiguration(TargetConfigurationSnapshot configuration)
        => Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public static TargetConfiguration Create(Guid deploymentId, TargetDefinition definition, DateTimeOffset now)
    {
        DomainGuard.Identity(deploymentId, nameof(DeploymentId));
        ArgumentNullException.ThrowIfNull(definition);
        var id = Guid.NewGuid();
        return new TargetConfiguration(Snapshot(id, 1, definition))
        {
            Id = id, DeploymentId = deploymentId, Revision = 1,
            CreatedAt = now, UpdatedAt = now
        };
    }

    public void UpdateConfiguration(TargetDefinition definition, DateTimeOffset now)
    {
        DomainGuard.Require(!IsDeleted, "A deleted target cannot be changed.");
        ArgumentNullException.ThrowIfNull(definition);
        var revision = checked(Revision + 1);
        Configuration = Snapshot(Id, revision, definition);
        Revision = revision;
        UpdatedAt = now;
    }

    public void Delete(DateTimeOffset now)
    {
        if (IsDeleted)
            return;
        Revision = checked(Revision + 1);
        IsDeleted = true;
        UpdatedAt = now;
    }

    private static TargetConfigurationSnapshot Snapshot(Guid id, long revision, TargetDefinition definition)
        => new(id, revision, definition.Provider, definition.ModuleId, definition.ModuleSource,
            definition.ModuleRevision, definition.Inputs, definition.SecretReferences);
}
