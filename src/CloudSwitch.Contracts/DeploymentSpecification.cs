namespace CloudSwitch.Contracts;

/// <summary>An immutable snapshot of the deployment submitted for execution, without credentials.</summary>
public sealed record DeploymentSpecification
{
    public required string Name { get; init; }
    public required string ContainerImage { get; init; }
    public required string Environment { get; init; }
    public required string Provider { get; init; }

    /// <summary>Container network port to expose and probe.</summary>
    public int ContainerPort { get; init; } = 80;

    /// <summary>Optional reference to image registry pull secret in secret store (e.g. vault://...).</summary>
    public string? ImagePullSecretReference { get; init; }

    /// <summary>CPU cores required for target compute instance.</summary>
    public double CpuCores { get; init; } = 1.0;

    /// <summary>Memory in megabytes required for target compute instance.</summary>
    public int MemoryMb { get; init; } = 512;

    /// <summary>Non-secret container environment variables.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>Secret references resolved at runtime via HashiCorp Vault (e.g. vault://path#key).</summary>
    public IReadOnlyDictionary<string, string>? SecretReferences { get; init; }

    /// <summary>Approved target configuration reference in PostgreSQL.</summary>
    public Guid? TargetConfigurationId { get; init; }

    /// <summary>Approved Terraform module name and version or revision.</summary>
    public string? TerraformModule { get; init; }

    /// <summary>Target health check policy for validation before traffic cutover.</summary>
    public HealthCheckPolicy? HealthCheck { get; init; }

    /// <summary>Public entrypoint domain name for traffic cutover (e.g. app.example.com).</summary>
    public string? EntrypointDomain { get; init; }

    /// <summary>Provider handling traffic routing / DNS cutover (e.g. Cloudflare, Route53).</summary>
    public string? TrafficRoutingProvider { get; init; }

    /// <summary>Source module definition or path for the target provider (e.g. modules/selectel/container-vm).</summary>
    public string? TargetModuleSource { get; init; }

    /// <summary>Pinned revision, tag or commit hash of the target module.</summary>
    public string? TargetModuleRevision { get; init; }

    /// <summary>Approved input parameters for the target Terraform module.</summary>
    public IReadOnlyDictionary<string, string>? TargetInputs { get; init; }

    /// <summary>Secret references specific to target provider resources (e.g. vault://path#key).</summary>
    public IReadOnlyDictionary<string, string>? TargetSecretReferences { get; init; }

    /// <summary>DNS zone or hosted zone reference for the traffic entrypoint.</summary>
    public string? TrafficZoneReference { get; init; }

    /// <summary>Vault credential reference for traffic/DNS provider API authentication.</summary>
    public string? TrafficCredentialReference { get; init; }

    /// <summary>Provider of the source deployment prior to migration.</summary>
    public string? SourceProvider { get; init; }

    /// <summary>Direct endpoint of the source deployment prior to migration.</summary>
    public string? SourceEndpoint { get; init; }

    /// <summary>Runtime revision of the source deployment at submission time.</summary>
    public long? SourceRuntimeRevision { get; init; }

    /// <summary>Configuration revision of the deployment at submission time.</summary>
    public long? ConfigurationRevision { get; init; }

    public bool Equals(DeploymentSpecification? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Name == other.Name
            && ContainerImage == other.ContainerImage
            && Environment == other.Environment
            && Provider == other.Provider
            && ContainerPort == other.ContainerPort
            && ImagePullSecretReference == other.ImagePullSecretReference
            && CpuCores.Equals(other.CpuCores)
            && MemoryMb == other.MemoryMb
            && TargetConfigurationId == other.TargetConfigurationId
            && TerraformModule == other.TerraformModule
            && Equals(HealthCheck, other.HealthCheck)
            && EntrypointDomain == other.EntrypointDomain
            && TrafficRoutingProvider == other.TrafficRoutingProvider
            && TargetModuleSource == other.TargetModuleSource
            && TargetModuleRevision == other.TargetModuleRevision
            && TrafficZoneReference == other.TrafficZoneReference
            && TrafficCredentialReference == other.TrafficCredentialReference
            && SourceProvider == other.SourceProvider
            && SourceEndpoint == other.SourceEndpoint
            && SourceRuntimeRevision == other.SourceRuntimeRevision
            && ConfigurationRevision == other.ConfigurationRevision
            && DictionariesEqual(Env, other.Env)
            && DictionariesEqual(SecretReferences, other.SecretReferences)
            && DictionariesEqual(TargetInputs, other.TargetInputs)
            && DictionariesEqual(TargetSecretReferences, other.TargetSecretReferences);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(ContainerImage);
        hash.Add(Environment);
        hash.Add(Provider);
        hash.Add(ContainerPort);
        hash.Add(ImagePullSecretReference);
        hash.Add(CpuCores);
        hash.Add(MemoryMb);
        hash.Add(TargetConfigurationId);
        hash.Add(TerraformModule);
        hash.Add(HealthCheck);
        hash.Add(EntrypointDomain);
        hash.Add(TrafficRoutingProvider);
        hash.Add(TargetModuleSource);
        hash.Add(TargetModuleRevision);
        hash.Add(TrafficZoneReference);
        hash.Add(TrafficCredentialReference);
        hash.Add(SourceProvider);
        hash.Add(SourceEndpoint);
        hash.Add(SourceRuntimeRevision);
        hash.Add(ConfigurationRevision);
        hash.Add(GetDictionaryHashCode(Env));
        hash.Add(GetDictionaryHashCode(SecretReferences));
        hash.Add(GetDictionaryHashCode(TargetInputs));
        hash.Add(GetDictionaryHashCode(TargetSecretReferences));
        return hash.ToHashCode();
    }

    private static int GetDictionaryHashCode(IReadOnlyDictionary<string, string>? dict)
    {
        if (dict is null || dict.Count == 0) return 0;
        int hash = dict.Count;
        foreach (var (k, v) in dict)
        {
            hash ^= HashCode.Combine(k.GetHashCode(StringComparison.Ordinal), v.GetHashCode(StringComparison.Ordinal));
        }
        return hash;
    }

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, string>? a,
        IReadOnlyDictionary<string, string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        int aCount = a?.Count ?? 0;
        int bCount = b?.Count ?? 0;
        if (aCount == 0 && bCount == 0) return true;
        if (a is null || b is null || a.Count != b.Count) return false;

        return a.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SequenceEqual(b.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }
}
