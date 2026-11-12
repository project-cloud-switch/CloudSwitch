using CloudSwitch.Persistence.Domain;
using Xunit;

namespace CloudSwitch.Api.Tests;

public sealed class DeploymentRevisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private static DeploymentDefinition Definition(string endpoint = "https://source.example.test")
        => new("production", "yandex", new Uri(endpoint),
            new ApplicationConfiguration("registry.example.test/app:1.0", 8080, 1000, 512,
                new Dictionary<string, string>(), new Dictionary<string, string>(), null),
            new HealthCheckPolicy("/healthz", 30, 5, 3, 2),
            new TrafficConfiguration("simulation", "app.example.test", null, null),
            new RecoveryPolicy(RecoveryMode.NotifyOnly, null));

    private static Deployment Registered() => Deployment.Register(Guid.NewGuid(), "app", Definition(), Now);

    [Fact]
    public void Register_starts_both_revisions_at_one()
    {
        var deployment = Registered();
        Assert.Equal(1, deployment.Revision);
        Assert.Equal(1, deployment.ObservationRevision);
    }

    [Fact]
    public void Policy_threshold_interval_and_enablement_changes_keep_observation_revision()
    {
        var deployment = Registered();
        deployment.UpdateMonitoring(new HealthCheckPolicy("/healthz", 60, 5, 5, 3),
            new RecoveryPolicy(RecoveryMode.NotifyOnly, null), false, Now.AddMinutes(1));
        Assert.Equal(2, deployment.Revision);
        Assert.Equal(1, deployment.ObservationRevision);
    }

    [Theory]
    [InlineData("/ready", 5)]
    [InlineData("/healthz", 10)]
    public void Path_or_timeout_change_raises_observation_revision(string path, int timeout)
    {
        var deployment = Registered();
        deployment.UpdateMonitoring(new HealthCheckPolicy(path, 30, timeout, 3, 2),
            new RecoveryPolicy(RecoveryMode.NotifyOnly, null), true, Now.AddMinutes(1));
        Assert.Equal(2, deployment.Revision);
        Assert.Equal(2, deployment.ObservationRevision);
    }

    [Fact]
    public void Full_update_with_new_source_endpoint_raises_observation_revision()
    {
        var deployment = Registered();
        deployment.UpdateConfiguration("app", Definition("https://other.example.test"), Now.AddMinutes(1));
        Assert.Equal(2, deployment.Revision);
        Assert.Equal(2, deployment.ObservationRevision);
    }

    [Fact]
    public void Monitoring_update_keeps_frozen_parts()
    {
        var original = Definition();
        var deployment = Deployment.Register(Guid.NewGuid(), "app", original, Now);
        deployment.UpdateMonitoring(new HealthCheckPolicy("/ready", 30, 5, 3, 2),
            new RecoveryPolicy(RecoveryMode.NotifyOnly, null), true, Now.AddMinutes(1));
        Assert.Equal(original.Environment, deployment.Definition.Environment);
        Assert.Equal(original.SourceProvider, deployment.Definition.SourceProvider);
        Assert.Equal(original.SourceEndpoint, deployment.Definition.SourceEndpoint);
        Assert.Same(original.Application, deployment.Definition.Application);
        Assert.Same(original.Traffic, deployment.Definition.Traffic);
    }

    [Fact]
    public void Deleted_deployment_rejects_monitoring_update()
    {
        var deployment = Registered();
        deployment.Delete(Now);
        Assert.Throws<DomainException>(() => deployment.UpdateMonitoring(
            new HealthCheckPolicy("/healthz", 30, 5, 3, 2),
            new RecoveryPolicy(RecoveryMode.NotifyOnly, null), true, Now.AddMinutes(1)));
    }
}