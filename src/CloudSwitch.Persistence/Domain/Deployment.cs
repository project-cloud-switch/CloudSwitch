using System.ComponentModel.DataAnnotations;

namespace CloudSwitch.Persistence.Domain;

public sealed record DeploymentDefinition
{
    public string Environment { get; }
    public string SourceProvider { get; }
    public Uri SourceEndpoint { get; }
    public ApplicationConfiguration Application { get; }
    public HealthCheckPolicy HealthCheck { get; }
    public TrafficConfiguration Traffic { get; }
    public RecoveryPolicy Recovery { get; }
    public bool MonitoringEnabled { get; }

    public DeploymentDefinition(string environment, string sourceProvider, Uri sourceEndpoint,
        ApplicationConfiguration application, HealthCheckPolicy healthCheck, TrafficConfiguration traffic,
        RecoveryPolicy? recovery = null, bool monitoringEnabled = true)
    {
        Environment = DomainGuard.Text(environment, nameof(Environment), 64);
        SourceProvider = DomainGuard.Text(sourceProvider, nameof(SourceProvider));
        SourceEndpoint = DomainGuard.Endpoint(sourceEndpoint);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(healthCheck);
        ArgumentNullException.ThrowIfNull(traffic);
        Application = application;
        HealthCheck = healthCheck;
        Traffic = traffic;
        Recovery = recovery ?? new RecoveryPolicy();
        MonitoringEnabled = monitoringEnabled;
    }
}

public sealed class Deployment
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    [MaxLength(120)]
    public string Name { get; private set; }
    public long Revision { get; private set; }
    public DeploymentDefinition Definition { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Deployment(string name, DeploymentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(definition);
        Name = name;
        Definition = definition;
    }

    public static Deployment Register(Guid userId, string name, DeploymentDefinition definition, DateTimeOffset now)
    {
        DomainGuard.Identity(userId, nameof(UserId));
        ArgumentNullException.ThrowIfNull(definition);
        return new Deployment(DomainGuard.Text(name, nameof(Name), 120).Trim(), definition)
        {
            Id = Guid.NewGuid(), UserId = userId, Revision = 1, CreatedAt = now, UpdatedAt = now
        };
    }

    public void UpdateConfiguration(string name, DeploymentDefinition definition, DateTimeOffset now)
    {
        DomainGuard.Require(!IsDeleted, "A deleted deployment cannot be changed.");
        var validatedName = DomainGuard.Text(name, nameof(Name), 120).Trim();
        ArgumentNullException.ThrowIfNull(definition);
        var revision = checked(Revision + 1);
        Name = validatedName;
        Definition = definition;
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
}
