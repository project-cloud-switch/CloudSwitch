using CloudSwitch.Persistence.Domain;
using Microsoft.Extensions.Options;

namespace CloudSwitch.Persistence;

public sealed class AccessOptions
{
    /// <summary>Zones the operator has delegated to users. Do not grant one zone to several users.</summary>
    public List<DnsZoneGrant> DnsZones { get; set; } = [];
}

public sealed class DnsZoneGrant
{
    public string Zone { get; set; } = "";
    public List<Guid> UserIds { get; set; } = [];
}

/// <summary>
/// Shared by API, Doctor (via job creation) and Worker: decides whether a user may use the given
/// Vault references and DNS zone. Pure function: no network and no secret values.
/// </summary>
public static class ReferenceAccess
{
    /// <summary>Returns the name of the first denied field, or null when everything is permitted.</summary>
    public static string? FindDenied(Guid userId, IEnumerable<(string Field, string Reference)> secrets,
        TrafficConfiguration? traffic, AccessOptions options, string trafficField = "definition.traffic")
    {
        foreach (var (field, reference) in secrets)
            if (!VaultReference.TryParse(reference, out var parsed) || !parsed.IsInUserNamespace(userId))
                return field;

        if (traffic is { Driver: "cloudflare-dns" } && !ZoneAllowed(userId, traffic, options))
            return trafficField;
        return null;
    }

    private static bool ZoneAllowed(Guid userId, TrafficConfiguration traffic, AccessOptions options)
        => DnsNames.TryCanonicalize(traffic.ZoneReference, out var zone)
            && DnsNames.TryCanonicalize(traffic.RecordName, out var record)
            && DnsNames.IsInZone(record, zone)
            && options.DnsZones.Any(grant => grant.UserIds.Contains(userId)
                && DnsNames.TryCanonicalize(grant.Zone, out var granted) && granted == zone);

    public static List<(string Field, string Reference)> Collect(DeploymentDefinition definition)
    {
        var list = definition.Application.SecretReferences
            .Select(pair => ($"definition.application.secretReferences.{pair.Key}", pair.Value)).ToList();
        if (definition.Application.ImagePullSecretReference is { } pull)
            list.Add(("definition.application.imagePullSecretReference", pull));
        if (definition.Traffic.CredentialReference is { } credential)
            list.Add(("definition.traffic.credentialReference", credential));
        return list;
    }

    public static List<(string Field, string Reference)> Collect(TargetDefinition target)
        => target.SecretReferences.Select(pair => ($"secretReferences.{pair.Key}", pair.Value)).ToList();

    /// <summary>The frozen references of a job. Worker calls this before resolving any secret.</summary>
    public static List<(string Field, string Reference)> Collect(MigrationSpecification migration)
    {
        var list = migration.Application.SecretReferences
            .Select(pair => ($"application.secretReferences.{pair.Key}", pair.Value)).ToList();
        if (migration.Application.ImagePullSecretReference is { } pull)
            list.Add(("application.imagePullSecretReference", pull));
        if (migration.Traffic.CredentialReference is { } credential)
            list.Add(("traffic.credentialReference", credential));
        list.AddRange(migration.Target.SecretReferences
            .Select(pair => ($"target.secretReferences.{pair.Key}", pair.Value)));
        return list;
    }
}

public sealed class AccessOptionsValidator : IValidateOptions<AccessOptions>
{
    public ValidateOptionsResult Validate(string? name, AccessOptions options)
    {
        var failures = new List<string>();
        var zones = new List<(string Zone, Guid User)>();
        for (var i = 0; i < options.DnsZones.Count; i++)
        {
            var grant = options.DnsZones[i];
            if (!DnsNames.TryCanonicalize(grant.Zone, out var zone))
            {
                failures.Add($"Access:DnsZones:{i}:Zone is not a valid DNS zone name.");
                continue;
            }
            if (grant.UserIds.Count != 1 || grant.UserIds[0] == Guid.Empty)
            {
                failures.Add($"Access:DnsZones:{i}: grant the zone to exactly one user.");
                continue;
            }
            zones.Add((zone, grant.UserIds[0]));
        }
        for (var i = 0; i < zones.Count; i++)
            for (var j = i + 1; j < zones.Count; j++)
                if (zones[i].User != zones[j].User
                    && (DnsNames.IsInZone(zones[i].Zone, zones[j].Zone) || DnsNames.IsInZone(zones[j].Zone, zones[i].Zone)))
                    failures.Add($"Zones '{zones[i].Zone}' and '{zones[j].Zone}' overlap and are granted to different users.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}