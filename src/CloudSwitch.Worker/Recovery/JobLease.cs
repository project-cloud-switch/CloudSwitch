using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;

namespace CloudSwitch.Worker.Recovery;

public sealed record JobLease(DeploymentRequested Request, string WorkerId, int Attempt,
    long FencingToken, DateTimeOffset LeaseExpiresAt, JobStatus PreviousStatus, long Sequence,
    MigrationPhase Phase, Uri? TargetEndpoint, CutoverOutcome Cutover, string? StateFingerprint = null)
{
    public Guid JobId => Request.JobId;
    public Guid DeploymentId => Request.DeploymentId;
}
