namespace CloudSwitch.Worker.Infrastructure;

public interface IVaultSecretResolver
{
    Task<string> ResolveSecretAsync(string vaultReference, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> ResolveSecretsAsync(
        IReadOnlyDictionary<string, string>? secretReferences,
        CancellationToken cancellationToken = default);
}

public sealed class DeterministicVaultResolver : IVaultSecretResolver
{
    public Task<string> ResolveSecretAsync(string vaultReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultReference);
        if (!vaultReference.StartsWith("vault://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid vault reference format '{vaultReference}'. Must start with vault://", nameof(vaultReference));

        // Deterministic simulation secret
        var clean = vaultReference.Replace("vault://", "").Replace('/', '-').Replace('#', '-');
        return Task.FromResult($"resolved-secret-for-{clean}");
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveSecretsAsync(
        IReadOnlyDictionary<string, string>? secretReferences,
        CancellationToken cancellationToken = default)
    {
        if (secretReferences is null || secretReferences.Count == 0)
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, reference) in secretReferences)
        {
            result[key] = await ResolveSecretAsync(reference, cancellationToken);
        }
        return result;
    }
}
