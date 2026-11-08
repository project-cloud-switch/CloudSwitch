namespace CloudSwitch.Persistence.Domain;

public sealed class DeploymentRuntime
{
    public Guid DeploymentId { get; private set; }
    public string Provider { get; private set; }
    public Uri Endpoint { get; private set; }
    public long Revision { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    private DeploymentRuntime(string provider, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(endpoint);
        Provider = provider;
        Endpoint = endpoint;
    }
}

public sealed class JobEvent
{
    public Guid EventId { get; private set; }
    public Guid JobId { get; private set; }
    public long Sequence { get; private set; }
    public JobProgress Event { get; private set; }
    private JobEvent(JobProgress @event)
        => Event = @event ?? throw new ArgumentNullException(nameof(@event));
}

public sealed class Incident
{
    public Guid Id { get; private set; }
    public Guid DeploymentId { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    private Incident() { }

    public static Incident Open(Guid deploymentId, DateTimeOffset now)
    {
        DomainGuard.Identity(deploymentId, nameof(DeploymentId));
        return new Incident { Id = Guid.NewGuid(), DeploymentId = deploymentId, StartedAt = now };
    }
}

public sealed class HealthObservation
{
    public Guid DeploymentId { get; private set; }
    public long ConfigurationRevision { get; private set; }
    public long RuntimeRevision { get; private set; }
    public long Sequence { get; private set; }
    public DeploymentHealthStatus Status { get; private set; }
    public Guid? IncidentId { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public int ConsecutiveSuccesses { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }
    private HealthObservation() { }

    public static HealthObservation Record(Guid deploymentId, long configurationRevision, long runtimeRevision,
        long sequence, DeploymentHealthStatus status, Guid? incidentId, int failures, int successes, DateTimeOffset now)
    {
        DomainGuard.Identity(deploymentId, nameof(DeploymentId));
        DomainGuard.Require(configurationRevision > 0 && runtimeRevision >= 0 && sequence > 0
            && failures >= 0 && successes >= 0 && Enum.IsDefined(status), "Health observation is invalid.");
        DomainGuard.Require(status != DeploymentHealthStatus.Unhealthy || incidentId is { } id && id != Guid.Empty,
            "An unhealthy observation requires an incident.");
        return new HealthObservation
        {
            DeploymentId = deploymentId, ConfigurationRevision = configurationRevision, RuntimeRevision = runtimeRevision,
            Sequence = sequence, Status = status, IncidentId = incidentId, ConsecutiveFailures = failures,
            ConsecutiveSuccesses = successes, ObservedAt = now
        };
    }
}
