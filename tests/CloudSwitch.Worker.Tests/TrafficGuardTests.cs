using CloudSwitch.Persistence.Domain;
using Xunit;

namespace CloudSwitch.Worker.Tests;

public sealed class TrafficGuardTests
{
    [Theory]
    [InlineData("api.example.com", "api.example.com")]
    [InlineData("API.EXAMPLE.COM", "api.example.com")]
    [InlineData("api.example.com.", "api.example.com")]
    [InlineData("  API.EXAMPLE.COM.  ", "api.example.com")]
    [InlineData("app.service.test..", "app.service.test")]
    [InlineData("pay.org.", "pay.org")]
    public void Traffic_guard_normalization_enforces_case_and_trailing_dot_equivalence(string input, string expected)
    {
        var normalized = TrafficGuard.NormalizeRecord(input);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Traffic_guard_normalization_rejects_empty_or_whitespace(string invalid)
    {
        Assert.Throws<DomainException>(() => TrafficGuard.NormalizeRecord(invalid));
    }

    [Fact]
    public void Traffic_guard_lifecycle_transitions_from_active_to_uncertain_and_cleared()
    {
        var deploymentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var guard = TrafficGuard.Create(
            deploymentId,
            "cloudflare",
            "zone-ref-123",
            "app.example.com.",
            jobId,
            now);

        Assert.Equal(TrafficGuardState.Active, guard.State);
        Assert.Equal(deploymentId, guard.DeploymentId);
        Assert.Equal(jobId, guard.ActiveJobId);
        Assert.Equal("cloudflare", guard.Provider);
        Assert.Equal("zone-ref-123", guard.ZoneReference);
        Assert.Equal("app.example.com.", guard.RecordName);
        Assert.Equal("app.example.com", guard.NormalizedRecord);
        Assert.Equal(now, guard.CreatedAt);
        Assert.Null(guard.ClearedAt);
        Assert.Null(guard.ClearedBy);
        Assert.Null(guard.Evidence);

        // Mark Uncertain
        var uncertainTime = now.AddMinutes(5);
        guard.MarkUncertain(uncertainTime, "DNS provider API timed out during cutover mutation.");

        Assert.Equal(TrafficGuardState.Uncertain, guard.State);
        Assert.Equal(uncertainTime, guard.UpdatedAt);
        Assert.Equal("DNS provider API timed out during cutover mutation.", guard.Evidence);

        // Mark Uncertain again on non-active throws DomainException
        Assert.Throws<DomainException>(() => guard.MarkUncertain(now.AddMinutes(6)));

        // Clear with verified evidence
        var clearedTime = now.AddMinutes(10);
        guard.Clear("operator@company.com", "Verified DNS query confirms traffic routes to target container.", clearedTime);

        Assert.Equal(TrafficGuardState.Cleared, guard.State);
        Assert.Equal("operator@company.com", guard.ClearedBy);
        Assert.Equal("Verified DNS query confirms traffic routes to target container.", guard.Evidence);
        Assert.Equal(clearedTime, guard.ClearedAt);
        Assert.Equal(clearedTime, guard.UpdatedAt);

        // Clearing an already cleared guard throws DomainException
        Assert.Throws<DomainException>(() => guard.Clear("operator", "second clear", now.AddMinutes(11)));
    }

    [Fact]
    public void Traffic_guard_clear_requires_operator_and_evidence()
    {
        var guard = TrafficGuard.Create(
            Guid.NewGuid(), "cloudflare", null, "test.example.com", Guid.NewGuid(), DateTimeOffset.UtcNow);

        guard.MarkUncertain(DateTimeOffset.UtcNow, "failed");

        Assert.Throws<DomainException>(() => guard.Clear("", "valid evidence description here", DateTimeOffset.UtcNow));
        Assert.Throws<DomainException>(() => guard.Clear("operator", "  ", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Traffic_guard_creation_validates_identities()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<DomainException>(() =>
            TrafficGuard.Create(Guid.Empty, "cloudflare", null, "app.test", Guid.NewGuid(), now));
        Assert.Throws<DomainException>(() =>
            TrafficGuard.Create(Guid.NewGuid(), "cloudflare", null, "app.test", Guid.Empty, now));
        Assert.Throws<DomainException>(() =>
            TrafficGuard.Create(Guid.NewGuid(), "", null, "app.test", Guid.NewGuid(), now));
        Assert.Throws<DomainException>(() =>
            TrafficGuard.Create(Guid.NewGuid(), "cloudflare", null, "", Guid.NewGuid(), now));
    }
}
