namespace CloudSwitch.Persistence.Domain;

public enum TrafficGuardState
{
    Active,
    Uncertain,
    Cleared
}

public sealed class TrafficGuard
{
    public Guid Id { get; private set; }
    public Guid DeploymentId { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string? ZoneReference { get; private set; }
    public string RecordName { get; private set; } = string.Empty;
    public string NormalizedRecord { get; private set; } = string.Empty;
    public Guid ActiveJobId { get; private set; }
    public TrafficGuardState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ClearedAt { get; private set; }
    public string? ClearedBy { get; private set; }
    public string? Evidence { get; private set; }

    private TrafficGuard() { }

    public static string NormalizeRecord(string recordName)
    {
        DomainGuard.Require(!string.IsNullOrWhiteSpace(recordName), "Record name is required.");
        return recordName.Trim().TrimEnd('.').ToLowerInvariant();
    }

    public static TrafficGuard Create(
        Guid deploymentId,
        string provider,
        string? zoneReference,
        string recordName,
        Guid activeJobId,
        DateTimeOffset now)
    {
        DomainGuard.Identity(deploymentId, nameof(DeploymentId));
        DomainGuard.Identity(activeJobId, nameof(ActiveJobId));
        var validProvider = DomainGuard.Text(provider, nameof(Provider), 128);
        var validRecord = DomainGuard.Text(recordName, nameof(RecordName), 256);
        var normalized = NormalizeRecord(validRecord);

        return new TrafficGuard
        {
            Id = Guid.NewGuid(),
            DeploymentId = deploymentId,
            Provider = validProvider.Trim(),
            ZoneReference = zoneReference?.Trim(),
            RecordName = validRecord.Trim(),
            NormalizedRecord = normalized,
            ActiveJobId = activeJobId,
            State = TrafficGuardState.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void MarkUncertain(DateTimeOffset now, string? reason = null)
    {
        DomainGuard.Require(State == TrafficGuardState.Active, "Only an active traffic guard can be marked uncertain.");
        State = TrafficGuardState.Uncertain;
        UpdatedAt = now;
        if (!string.IsNullOrWhiteSpace(reason))
            Evidence = reason.Trim();
    }

    public void Clear(string clearedBy, string evidence, DateTimeOffset now)
    {
        DomainGuard.Text(clearedBy, nameof(ClearedBy), 256);
        DomainGuard.Text(evidence, nameof(Evidence), 2048);
        DomainGuard.Require(State != TrafficGuardState.Cleared, "Traffic guard is already cleared.");

        State = TrafficGuardState.Cleared;
        ClearedBy = clearedBy.Trim();
        Evidence = evidence.Trim();
        ClearedAt = now;
        UpdatedAt = now;
    }
}
