using System.Net.Http.Headers;
using System.Text.Json;

namespace CloudSwitch.Worker.Infrastructure;

public sealed class VaultOptions
{
    public Uri? Address { get; set; }
    public string? Token { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class ScopedVaultSecretResolver : IVaultSecretResolver, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public ScopedVaultSecretResolver(VaultOptions options, HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Address is null)
            throw new ArgumentException("Vault Address is required.", nameof(options));

        _ownsClient = client is null;
        _client = client ?? new HttpClient { BaseAddress = options.Address, Timeout = options.Timeout };

        if (!string.IsNullOrWhiteSpace(options.Token))
            _client.DefaultRequestHeaders.Add("X-Vault-Token", options.Token);
    }

    public async Task<string> ResolveSecretAsync(string vaultReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultReference);
        if (!vaultReference.StartsWith("vault://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid Vault reference: '{vaultReference}'. Must begin with 'vault://'", nameof(vaultReference));

        var withoutScheme = vaultReference["vault://".Length..];
        var parts = withoutScheme.Split('#', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new ArgumentException($"Invalid Vault reference '{vaultReference}'. Expected format 'vault://path#key'.", nameof(vaultReference));

        var path = parts[0].TrimStart('/');
        var key = parts[1];

        var response = await _client.GetAsync($"v1/{path}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to resolve Vault reference '{vaultReference}'. Vault returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var dataElement))
            throw new InvalidOperationException($"Vault response for '{path}' does not contain 'data' property.");

        // Check if KV v2 (data.data) or KV v1 (data)
        if (dataElement.TryGetProperty("data", out var kv2Data))
            dataElement = kv2Data;

        if (dataElement.TryGetProperty(key, out var valueElement))
            return valueElement.GetString() ?? throw new InvalidOperationException($"Vault key '{key}' at '{path}' is null.");

        throw new KeyNotFoundException($"Key '{key}' was not found in Vault secret at '{path}'.");
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

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }
}
