using CloudSwitch.Persistence;
using CloudSwitch.Persistence.Domain;
using Xunit;

namespace CloudSwitch.Api.Tests;

public sealed class ReferenceAccessTests
{
    private static readonly Guid Owner = Guid.Parse("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");
    private static readonly Guid Other = Guid.Parse("b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e");
    private static readonly AccessOptions NoZones = new();

    private static string Own(string rest = "app#key") => $"vault://users/{Owner:D}/{rest}";
    private static string? Denied(string reference) => ReferenceAccess.FindDenied(Owner, [("field", reference)], null, NoZones);

    private static AccessOptions Zones(string zone, Guid user)
        => new() { DnsZones = [new DnsZoneGrant { Zone = zone, UserIds = [user] }] };

    private static TrafficConfiguration Cloudflare(string zone, string record)
        => new("cloudflare-dns", record, zone, Own("dns#token"));

    [Fact]
    public void Own_namespace_is_permitted() => Assert.Null(Denied(Own()));

    [Fact]
    public void Foreign_lookalike_and_non_canonical_references_are_denied()
    {
        foreach (var reference in new[]
        {
            $"vault://users/{Other:D}/app#key",
            $"vault://users/{Owner:D}x/app#key",
            $"vault://users/{Owner.ToString("D").ToUpperInvariant()}/app#key",
            $"vault://users/{Owner:D}/../{Other:D}/app#key",
            $"vault://users/{Owner:N}/app#key",
            $"vault://usersX/{Owner:D}/app#key",
            "vault://demo#key",
            "vault://users#key",
            ""
        })
            Assert.Equal("field", Denied(reference));
    }

    [Fact]
    public void Dns_zone_requires_a_grant_and_a_record_inside_the_zone()
    {
        Assert.Null(ReferenceAccess.FindDenied(Owner, [], Cloudflare("example.test", "app.example.test"), Zones("example.test", Owner)));
        Assert.Null(ReferenceAccess.FindDenied(Owner, [], Cloudflare("Example.TEST.", "APP.example.test"), Zones("example.test", Owner)));

        foreach (var (traffic, options) in new[]
        {
            (Cloudflare("example.test", "app.example.test"), NoZones),                               // зона отозвана
            (Cloudflare("example.test", "app.example.test"), Zones("example.test", Other)),          // зона чужая
            (Cloudflare("other.test", "app.other.test"), Zones("example.test", Owner)),              // зона не выдана
            (Cloudflare("example.test", "app.other.test"), Zones("example.test", Owner)),            // запись вне зоны
            (Cloudflare("example.test", "evilexample.test"), Zones("example.test", Owner)),          // похожее имя
            (Cloudflare("*.example.test", "app.example.test"), Zones("example.test", Owner))          // шаблон
        })
            Assert.Equal("definition.traffic", ReferenceAccess.FindDenied(Owner, [], traffic, options));
    }

    [Fact]
    public void Simulation_driver_does_not_need_a_zone()
        => Assert.Null(ReferenceAccess.FindDenied(Owner, [],
            new TrafficConfiguration("simulation", "app.example.test", null, null), NoZones));

    [Fact]
    public void Frozen_snapshot_references_are_collected_for_the_worker()
    {
        var target = new TargetConfigurationSnapshot(Guid.NewGuid(), 1, "local", "local-stateless-v1", "source", "rev",
            new Dictionary<string, string>(), new Dictionary<string, string> { ["cloud"] = $"vault://users/{Other:D}/cloud#key" });
        var migration = new MigrationSpecification(1,
            new SourceDeploymentSnapshot("yandex", new Uri("https://app.example.test"), 0), target,
            new ApplicationConfiguration("nginx@sha256:" + new string('a', 64), 80, 500, 256,
                new Dictionary<string, string>(), new Dictionary<string, string> { ["API_KEY"] = Own() }, Own("registry#token")),
            new HealthCheckPolicy("/health", 10, 3, 3, 2),
            new TrafficConfiguration("simulation", "app.example.test", null, null));

        Assert.Equal("target.secretReferences.cloud",
            ReferenceAccess.FindDenied(Owner, ReferenceAccess.Collect(migration), migration.Traffic, NoZones, "traffic"));
    }
}