namespace CloudSwitch.Persistence.Domain;

public enum JobStatus { Pending, Running, Succeeded, Failed }
public enum JobTrigger { Manual, Automatic }
public enum MigrationPhase { Pending, Provisioning, TargetHealth, TrafficCutover, EntrypointVerification, Completed }
public enum CutoverOutcome { NotStarted, Applied, Verified, Uncertain }
public enum RecoveryMode { NotifyOnly, Automatic }
public enum DeploymentHealthStatus { Unknown, Healthy, Unhealthy }

public sealed record SourceDeploymentSnapshot(string Provider, Uri Endpoint, long RuntimeRevision);

public sealed record MigrationSpecification(long ConfigurationRevision, SourceDeploymentSnapshot Source,
    TargetConfigurationSnapshot Target, ApplicationConfiguration Application, HealthCheckPolicy TargetHealthCheck,
    TrafficConfiguration Traffic);

public sealed record DeploymentSpecification(string Name, string ContainerImage, string Environment,
    string Provider, MigrationSpecification? Migration = null);

public sealed record JobRequestSnapshot(Guid EventId, Guid CorrelationId, DateTimeOffset OccurredAt,
    Guid JobId, Guid DeploymentId, Guid UserId, DeploymentSpecification Specification,
    JobTrigger Trigger = JobTrigger.Manual, Guid? IncidentId = null);

public sealed record JobProgress(Guid EventId, Guid CorrelationId, DateTimeOffset OccurredAt, Guid JobId,
    Guid DeploymentId, Guid UserId, JobStatus PreviousStatus, JobStatus Status, int Attempt, long Sequence,
    string? ErrorCode = null, string? ErrorMessage = null, MigrationPhase? Phase = null,
    Uri? TargetEndpoint = null, CutoverOutcome? Cutover = null);

public sealed record JobExecutionCheckpoint(string? StateFingerprint = null);

public sealed class Job
{
    public Guid Id { get; private set; }
    public Guid DeploymentId { get; private set; }
    public Guid UserId { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public Guid TargetConfigurationId { get; private set; }
    public Guid? IncidentId { get; private set; }
    public JobTrigger Trigger { get; private set; }
    public JobRequestSnapshot Request { get; private set; }
    public JobStatus Status { get; private set; }
    public MigrationPhase Phase { get; private set; }
    public int Attempt { get; private set; }
    public long Sequence { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public long FencingToken { get; private set; }
    public JobExecutionCheckpoint Checkpoints { get; private set; } = new();
    public Uri? TargetEndpoint { get; private set; }
    public CutoverOutcome Cutover { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Job(JobRequestSnapshot request)
        => Request = request ?? throw new ArgumentNullException(nameof(request));

    public bool CanAcquireLease(DateTimeOffset now)
        => Status is JobStatus.Pending or JobStatus.Running && (LeaseExpiresAt is null || LeaseExpiresAt <= now);

    public bool OwnsLease(string owner, long fencingToken, DateTimeOffset now)
        => Status is JobStatus.Pending or JobStatus.Running && LeaseOwner == owner && FencingToken == fencingToken && LeaseExpiresAt > now;

    public void AcquireLease(string owner, TimeSpan duration, DateTimeOffset now)
    {
        DomainGuard.Require(CanAcquireLease(now), "The job is already leased or terminal.");
        var nextOwner = DomainGuard.Text(owner, nameof(LeaseOwner), 256);
        DomainGuard.Require(duration > TimeSpan.Zero, "Lease duration must be positive.");
        var nextAttempt = checked(Attempt + 1);
        var nextFence = checked(FencingToken + 1);
        var expiresAt = now.Add(duration);
        LeaseOwner = nextOwner;
        Attempt = nextAttempt;
        FencingToken = nextFence;
        LeaseExpiresAt = expiresAt;
        UpdatedAt = now;
    }

    public void RenewLease(TimeSpan duration, DateTimeOffset now)
    {
        DomainGuard.Require(Status is JobStatus.Pending or JobStatus.Running && LeaseExpiresAt > now && duration > TimeSpan.Zero,
            "Only an active lease can be renewed.");
        LeaseExpiresAt = now.Add(duration);
        UpdatedAt = now;
    }

    public JobProgress RecordProgress(JobStatus status, MigrationPhase phase, CutoverOutcome cutover,
        Uri? endpoint, string? errorCode, string? errorMessage, DateTimeOffset now, JobStatus? previousStatus = null, string? stateFingerprint = null)
    {
        DomainGuard.Require(Status is JobStatus.Pending or JobStatus.Running && Enum.IsDefined(phase) && Enum.IsDefined(cutover)
            && status is JobStatus.Running or JobStatus.Succeeded or JobStatus.Failed, "Job progress is invalid.");
        DomainGuard.Require(status != JobStatus.Succeeded || phase == MigrationPhase.Completed
            && cutover == CutoverOutcome.Verified && endpoint is not null, "Success requires verified traffic and a target endpoint.");
        var previous = previousStatus ?? Status;
        Sequence = checked(Sequence + 1);
        Checkpoints = new JobExecutionCheckpoint(stateFingerprint);
        Status = status;
        Phase = phase;
        Cutover = cutover;
        TargetEndpoint = endpoint;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        UpdatedAt = now;
        if (status is JobStatus.Succeeded or JobStatus.Failed)
        {
            LeaseOwner = null;
            LeaseExpiresAt = null;
        }
        return new JobProgress(Guid.NewGuid(), Request.CorrelationId, now, Id, DeploymentId, UserId,
            previous, status, Attempt, Sequence, errorCode, errorMessage, phase, TargetEndpoint, cutover);
    }

    public static Job Create(Guid deploymentId, Guid userId, DeploymentSpecification specification, JobTrigger trigger,
        string? idempotencyKey, Guid? incidentId, Guid correlationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(specification);
        DomainGuard.Identity(deploymentId, nameof(DeploymentId));
        DomainGuard.Identity(userId, nameof(UserId));
        DomainGuard.Identity(correlationId, nameof(correlationId));
        DomainGuard.Require(Enum.IsDefined(trigger), "Job trigger is invalid.");
        var migration = specification.Migration
            ?? throw new DomainException("invalid_domain_state", "A new migration job requires a complete snapshot.");
        ArgumentNullException.ThrowIfNull(migration.Source);
        ArgumentNullException.ThrowIfNull(migration.Target);
        ArgumentNullException.ThrowIfNull(migration.Application);
        ArgumentNullException.ThrowIfNull(migration.TargetHealthCheck);
        ArgumentNullException.ThrowIfNull(migration.Traffic);
        DomainGuard.Require(trigger != JobTrigger.Manual || !string.IsNullOrWhiteSpace(idempotencyKey)
            && idempotencyKey.Length <= 128 && incidentId is null, "Manual jobs require an idempotency key and no incident.");
        DomainGuard.Require(trigger != JobTrigger.Automatic || incidentId is { } incident && incident != Guid.Empty
            && idempotencyKey is null, "Automatic jobs require an incident and no manual submission key.");
        DomainGuard.Require(migration.ConfigurationRevision > 0 && migration.Source.RuntimeRevision >= 0,
            "Migration snapshot revisions are invalid.");
        DomainGuard.Require(specification.ContainerImage == migration.Application.Image
            && specification.Provider == migration.Target.Provider, "Migration snapshot fields must agree.");
        DomainGuard.Require(migration.Source.Provider != migration.Target.Provider,
            "A migration target must differ from the active provider.");
        var id = Guid.NewGuid();
        var request = new JobRequestSnapshot(Guid.NewGuid(), correlationId, now, id, deploymentId, userId,
            specification, trigger, incidentId);
        return new Job(request)
        {
            Id = id, DeploymentId = deploymentId, UserId = userId, IdempotencyKey = idempotencyKey,
            TargetConfigurationId = migration.Target.Id, IncidentId = incidentId, Trigger = trigger,
            Status = JobStatus.Pending, Phase = MigrationPhase.Pending, CreatedAt = now, UpdatedAt = now
        };
    }
}
