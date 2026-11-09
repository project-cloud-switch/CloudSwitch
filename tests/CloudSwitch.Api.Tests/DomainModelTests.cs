using System.Text.Json;
using System.Text.Json.Nodes;
using CloudSwitch.Persistence;
using Xunit;
using Domain = CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Api.Tests;

public sealed class DomainModelTests
{
    private static Domain.DeploymentDefinition Definition(IReadOnlyDictionary<string, string>? settings = null)
        => new("development", "yandex", new Uri("https://app.example.test"),
            new Domain.ApplicationConfiguration("nginx@sha256:" + new string('a', 64), 80, 500, 256, settings),
            new Domain.HealthCheckPolicy("/health", 10, 3, 3, 2),
            new Domain.TrafficConfiguration("simulation", "app.example.test"));

    private static Domain.TargetDefinition TargetDefinition(IReadOnlyDictionary<string, string>? inputs = null)
        => new("local", "local-stateless-v1", "modules/local/stateless", "local-simulation-v1", inputs);

    [Fact]
    public void Deployment_controls_revisions_timestamps_and_deletion()
    {
        var now = DateTimeOffset.UtcNow;
        var ownerId = Guid.NewGuid();
        var deployment = Domain.Deployment.Register(ownerId, " demo ", Definition(), now);
        Assert.Equal("demo", deployment.Name);
        Assert.Equal(1, deployment.Revision);
        deployment.UpdateConfiguration(" renamed ", Definition(), now.AddMinutes(1));
        Assert.Equal("renamed", deployment.Name);
        Assert.Equal(2, deployment.Revision);
        Assert.Equal(ownerId, deployment.UserId);
        Assert.Equal(now, deployment.CreatedAt);
        Assert.Equal(now.AddMinutes(1), deployment.UpdatedAt);
        deployment.Delete(now.AddMinutes(2));
        deployment.Delete(now.AddMinutes(3));
        Assert.True(deployment.IsDeleted);
        Assert.Equal(3, deployment.Revision);
        Assert.Equal(now.AddMinutes(2), deployment.UpdatedAt);
        Assert.Throws<Domain.DomainException>(() => deployment.UpdateConfiguration("changed", Definition(), now.AddMinutes(4)));
    }

    [Fact]
    public void Invalid_changes_preserve_the_existing_state()
    {
        var now = DateTimeOffset.UtcNow;
        var deployment = Domain.Deployment.Register(Guid.NewGuid(), "demo", Definition(), now);
        Assert.Throws<Domain.DomainException>(() => deployment.UpdateConfiguration(" ", Definition(), now.AddMinutes(1)));
        Assert.Equal("demo", deployment.Name);
        Assert.Equal(1, deployment.Revision);
        Assert.Equal(now, deployment.UpdatedAt);
        Assert.Throws<Domain.DomainException>(() => Domain.Deployment.Register(Guid.Empty, "demo", Definition(), now));
        Assert.Throws<Domain.DomainException>(() => new Domain.RecoveryPolicy(Domain.RecoveryMode.Automatic));
        Assert.Throws<Domain.DomainException>(() => new Domain.HealthCheckPolicy("//untrusted.example", 10, 3, 3, 2));
        Assert.Throws<Domain.DomainException>(() => new Domain.HealthCheckPolicy("/health", 3, 10, 3, 2));
    }

    [Fact]
    public void Target_updates_create_new_snapshots_with_consistent_identity_and_revision()
    {
        var now = DateTimeOffset.UtcNow;
        var inputs = new Dictionary<string, string> { ["region"] = "first" };
        var target = Domain.TargetConfiguration.Create(Guid.NewGuid(), TargetDefinition(inputs), now);
        var original = target.Configuration;
        inputs["region"] = "changed-outside";
        Assert.Equal("first", target.Configuration.Inputs["region"]);
        target.UpdateConfiguration(TargetDefinition(new Dictionary<string, string> { ["region"] = "second" }), now.AddMinutes(1));
        Assert.Equal(2, target.Revision);
        Assert.Equal(target.Id, target.Configuration.Id);
        Assert.Equal(target.Revision, target.Configuration.Revision);
        Assert.Equal("second", target.Configuration.Inputs["region"]);
        Assert.Equal(1, original.Revision);
        Assert.Equal("first", original.Inputs["region"]);
        target.Delete(now.AddMinutes(2));
        Assert.Throws<Domain.DomainException>(() => target.UpdateConfiguration(TargetDefinition(), now.AddMinutes(3)));
    }

    [Fact]
    public void Domain_configuration_is_isolated_from_mutable_inputs_and_exported_contracts()
    {
        var settings = new Dictionary<string, string> { ["LOG_LEVEL"] = "info" };
        var application = Definition(settings).Application;
        settings["LOG_LEVEL"] = "debug";
        Assert.Equal("info", application.Settings["LOG_LEVEL"]);
        var contract = application.ToContract();
        contract.Settings["LOG_LEVEL"] = "trace";
        Assert.Equal("info", application.Settings["LOG_LEVEL"]);
        Assert.Throws<Domain.DomainException>(() => new Domain.ApplicationConfiguration(
            "nginx:latest", 80, 500, 256));
        Assert.Throws<Domain.DomainException>(() => new Domain.ApplicationConfiguration(
            application.Image, 80, 500, 256, secretReferences: new Dictionary<string, string> { ["KEY"] = "plain-password" }));
    }

    [Fact]
    public void Configuration_values_reject_trailing_newlines()
    {
        var image = Definition().Application.Image;
        Assert.Throws<Domain.DomainException>(() => new Domain.ApplicationConfiguration(image + "\n", 80, 500, 256));
        Assert.Throws<Domain.DomainException>(() => Definition(new Dictionary<string, string> { ["KEY\n"] = "value" }));
        Assert.Throws<Domain.DomainException>(() => new Domain.ApplicationConfiguration(image, 80, 500, 256,
            secretReferences: new Dictionary<string, string> { ["KEY"] = "secret://demo/key\n" }));
    }

    [Fact]
    public void Job_creation_captures_an_immutable_consistent_snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var deployment = Domain.Deployment.Register(Guid.NewGuid(), "demo", Definition(), now);
        var target = Domain.TargetConfiguration.Create(deployment.Id, TargetDefinition(), now);
        var definition = deployment.Definition;
        var specification = new Domain.DeploymentSpecification(deployment.Name, definition.Application.Image,
            definition.Environment, target.Configuration.Provider,
            new Domain.MigrationSpecification(deployment.Revision,
                new Domain.SourceDeploymentSnapshot(definition.SourceProvider, definition.SourceEndpoint, 0),
                target.Configuration, definition.Application, definition.HealthCheck, definition.Traffic));
        var correlationId = Guid.NewGuid();
        var job = Domain.Job.Create(deployment.Id, deployment.UserId, specification, Domain.JobTrigger.Manual,
            "first", null, correlationId, now);
        Assert.Equal(job.Id, job.Request.JobId);
        Assert.Equal(correlationId, job.Request.CorrelationId);
        Assert.Equal(Domain.JobStatus.Pending, job.Status);
        target.UpdateConfiguration(TargetDefinition(), now.AddMinutes(1));
        deployment.UpdateConfiguration("changed", Definition(), now.AddMinutes(1));
        Assert.Equal(1, job.Request.Specification.Migration!.ConfigurationRevision);
        Assert.Equal(1, job.Request.Specification.Migration.Target.Revision);
        Assert.Equal("demo", job.Request.Specification.Name);
        Assert.Throws<Domain.DomainException>(() => Domain.Job.Create(deployment.Id, deployment.UserId,
            specification with { ContainerImage = "different" }, Domain.JobTrigger.Manual, "second", null, correlationId, now));
        Assert.Throws<Domain.DomainException>(() => Domain.Job.Create(deployment.Id, deployment.UserId,
            specification with { Migration = null }, Domain.JobTrigger.Manual, "second", null, correlationId, now));
    }

    [Theory]
    [InlineData("deployment-requested.v1.json")]
    [InlineData("deployment-requested.v2-legacy.json")]
    public void Existing_persisted_snapshots_map_to_the_agreed_contract(string fixture)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        var options = CloudSwitchDbContext.JsonOptions;
        var domain = JsonSerializer.Deserialize<Domain.JobRequestSnapshot>(json, options)!;
        var original = JsonSerializer.Deserialize<CloudSwitch.Contracts.Events.DeploymentRequested>(json, options)!;
        var mapped = domain.ToContract();
        Assert.Equal(original.JobId, mapped.JobId);
        Assert.Equal(original.Specification.ContainerImage, mapped.Specification.ContainerImage);
        Assert.Equal(original.Trigger, mapped.Trigger);
        if (domain.Specification.Migration is { } migration)
        {
            Assert.Equal(migration.Target.Id, mapped.Specification.TargetConfigurationId);
            Assert.Equal(migration.Application.Port, mapped.Specification.ContainerPort);
            Assert.Equal(migration.Application.CpuMillicores / 1000d, mapped.Specification.CpuCores);
            Assert.Equal(migration.Traffic.RecordName, mapped.Specification.EntrypointDomain);
        }
    }

    [Fact]
    public void Optional_configuration_fields_have_explicit_defaults()
    {
        var definition = Definition();
        Assert.Equal(Domain.RecoveryMode.NotifyOnly, definition.Recovery.Mode);
        Assert.Null(definition.Recovery.TargetConfigurationId);
        Assert.Empty(definition.Application.Settings);
        Assert.Empty(definition.Application.SecretReferences);
        Assert.Null(definition.Traffic.ZoneReference);
        Assert.Null(definition.Traffic.CredentialReference);
        Assert.Null(Domain.User.Register("https://identity.example.test", "opaque-subject", null, DateTimeOffset.UtcNow).Username);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("application")]
    public void Required_snapshot_fields_reject_null_and_missing_values(string field)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "deployment-requested.v2-legacy.json"));
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        var specification = Assert.IsType<JsonObject>(root["specification"]);
        var migration = Assert.IsType<JsonObject>(specification["migration"]);
        migration[field] = null;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Domain.JobRequestSnapshot>(root.ToJsonString(), CloudSwitchDbContext.JsonOptions));
        migration.Remove(field);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Domain.JobRequestSnapshot>(root.ToJsonString(), CloudSwitchDbContext.JsonOptions));
    }

    [Fact]
    public void Aggregate_state_has_no_public_setters()
    {
        foreach (var type in new[] { typeof(Domain.Deployment), typeof(Domain.TargetConfiguration), typeof(Domain.Job), typeof(Domain.User) })
            Assert.All(type.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic ?? false));
    }
}
