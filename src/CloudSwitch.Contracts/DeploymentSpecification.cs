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
            && Math.Abs(CpuCores - other.CpuCores) < 0.0001
            && MemoryMb == other.MemoryMb
            && TargetConfigurationId == other.TargetConfigurationId
            && TerraformModule == other.TerraformModule
            && Equals(HealthCheck, other.HealthCheck)
            && EntrypointDomain == other.EntrypointDomain
            && TrafficRoutingProvider == other.TrafficRoutingProvider
            && DictionariesEqual(Env, other.Env)
            && DictionariesEqual(SecretReferences, other.SecretReferences);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(ContainerImage);
        hash.Add(Environment);
        hash.Add(Provider);
        hash.Add(ContainerPort);
        hash.Add(CpuCores);
        hash.Add(MemoryMb);
        hash.Add(TargetConfigurationId);
        hash.Add(TerraformModule);
        hash.Add(HealthCheck);
        hash.Add(EntrypointDomain);
        hash.Add(TrafficRoutingProvider);
        return hash.ToHashCode();
    }

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, string>? a,
        IReadOnlyDictionary<string, string>? b)
    {
        if (a is null && b is null) return true;
        if ((a is null || a.Count == 0) && (b is null || b.Count == 0)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var bVal) || bVal != value) return false;
        }

        return true;
    }
}

