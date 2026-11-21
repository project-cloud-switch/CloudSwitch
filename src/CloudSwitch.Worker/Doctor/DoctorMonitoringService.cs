using CloudSwitch.Contracts.Events;
using CloudSwitch.Persistence;
using CloudSwitch.Persistence.Domain;
using CloudSwitch.Worker.Probing;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudSwitch.Worker.Doctor;

public sealed class DoctorMonitoringService(
    IServiceScopeFactory scopes,
    IHttpHealthProber prober,
    IOptions<DoctorOptions> options,
    ILogger<DoctorMonitoringService> logger) : BackgroundService
{
    public async Task PollDeploymentsOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudSwitchDbContext>();
        var submission = scope.ServiceProvider.GetRequiredService<JobSubmissionService>();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        var enabledDeployments = await db.Deployments
            .Where(d => d.Definition.MonitoringEnabled && !d.IsDeleted)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;

        foreach (var deployment in enabledDeployments)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                await MonitorDeploymentAsync(deployment, db, submission, publisher, now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Doctor monitoring failed for DeploymentId={DeploymentId}.", deployment.Id);
            }
        }
    }

    private async Task MonitorDeploymentAsync(
        Deployment deployment,
        CloudSwitchDbContext db,
        JobSubmissionService submission,
        IPublishEndpoint publisher,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var policy = deployment.Definition.HealthCheck;
        if (policy is null)
            return;

        await using var transaction = await DeploymentTransactions.BeginAsync(db, deployment.Id, ct);

        var runtime = await db.Runtimes.SingleOrDefaultAsync(r => r.DeploymentId == deployment.Id, ct);
        var activeProvider = runtime?.Provider ?? deployment.Definition.SourceProvider;
        var activeEndpoint = runtime?.Endpoint ?? deployment.Definition.SourceEndpoint;
        var runtimeRevision = runtime?.Revision ?? 0;

        var path = policy.Path.StartsWith('/') ? policy.Path : "/" + policy.Path;
        var probeUri = new Uri(activeEndpoint, path);

        var probeTimeout = TimeSpan.FromSeconds(policy.TimeoutSeconds > 0 ? policy.TimeoutSeconds : 3);
        var probeResult = await prober.ProbeAsync(probeUri, probeTimeout, ct);

        var observation = await db.Health.SingleOrDefaultAsync(h => h.DeploymentId == deployment.Id, ct);
        var openIncident = await db.Incidents
            .Where(i => i.DeploymentId == deployment.Id && i.ResolvedAt == null)
            .OrderByDescending(i => i.StartedAt)
            .FirstOrDefaultAsync(ct);

        var previousStatus = observation?.Status ?? DeploymentHealthStatus.Unknown;
        var previousFailures = observation?.ConsecutiveFailures ?? 0;
        var previousSuccesses = observation?.ConsecutiveSuccesses ?? 0;
        var currentSequence = observation?.Sequence ?? 0;

        int nextFailures;
        int nextSuccesses;
        DeploymentHealthStatus nextStatus;
        Guid? nextIncidentId = openIncident?.Id;

        if (probeResult.IsHealthy)
        {
            nextFailures = 0;
            nextSuccesses = previousSuccesses + 1;
            if (nextSuccesses >= policy.RecoveryThreshold)
            {
                nextStatus = DeploymentHealthStatus.Healthy;
                if (openIncident is not null)
                {
                    openIncident.Resolve(now);
                    nextIncidentId = null;
                }
            }
            else
            {
                nextStatus = previousStatus;
            }
        }
        else
        {
            nextSuccesses = 0;
            nextFailures = previousFailures + 1;
            if (nextFailures >= policy.FailureThreshold)
            {
                nextStatus = DeploymentHealthStatus.Unhealthy;
                if (openIncident is null)
                {
                    var newIncident = Incident.Open(deployment.Id, now);
                    db.Incidents.Add(newIncident);
                    nextIncidentId = newIncident.Id;
                }
                else
                {
                    nextIncidentId = openIncident.Id;
                }
            }
            else
            {
                nextStatus = previousStatus;
            }
        }

        var nextSequence = currentSequence + 1;

        if (observation is null)
        {
            db.Health.Add(HealthObservation.Record(
                deployment.Id,
                deployment.Revision,
                runtimeRevision,
                nextSequence,
                nextStatus,
                nextIncidentId,
                nextFailures,
                nextSuccesses,
                now));
        }
        else
        {
            observation.Update(
                deployment.Revision,
                runtimeRevision,
                nextSequence,
                nextStatus,
                nextIncidentId,
                nextFailures,
                nextSuccesses,
                now);
        }

        // Publish transition event if health status changed or outage opened/resolved
        if (nextStatus != previousStatus || (nextIncidentId != openIncident?.Id))
        {
            var healthEvent = new DeploymentHealthChanged
            {
                EventId = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid(),
                OccurredAt = now,
                DeploymentId = deployment.Id,
                UserId = deployment.UserId,
                Provider = activeProvider,
                PreviousStatus = ContractMapping.ConvertEnum<CloudSwitch.Contracts.DeploymentHealthStatus>(previousStatus),
                Status = ContractMapping.ConvertEnum<CloudSwitch.Contracts.DeploymentHealthStatus>(nextStatus),
                IncidentId = nextIncidentId ?? openIncident?.Id,
                Sequence = nextSequence,
                ConsecutiveFailures = nextFailures,
                ReasonCode = probeResult.IsHealthy ? null : probeResult.Category.ToString()
            };
            await publisher.Publish(healthEvent, ct);
        }

        // Automatic recovery job creation: only in Automatic mode, Unhealthy status, with an open incident
        if (nextStatus == DeploymentHealthStatus.Unhealthy && nextIncidentId is { } incidentId && incidentId != Guid.Empty)
        {
            if (deployment.Definition.Recovery.Mode == RecoveryMode.Automatic
                && deployment.Definition.Recovery.TargetConfigurationId is { } targetId)
            {
                var jobExists = await db.Jobs.AnyAsync(
                    j => j.DeploymentId == deployment.Id && j.IncidentId == incidentId, ct);

                if (!jobExists)
                {
                    try
                    {
                        await submission.SubmitAsync(new SubmitJob(
                            DeploymentId: deployment.Id,
                            UserId: deployment.UserId,
                            TargetConfigurationId: targetId,
                            Trigger: JobTrigger.Automatic,
                            IdempotencyKey: null,
                            IncidentId: incidentId,
                            CorrelationId: Guid.NewGuid(),
                            HealthSequence: nextSequence
                        ), ct);
                    }
                    catch (OperationRejectedException ex)
                    {
                        logger.LogWarning("Automatic recovery job rejected for IncidentId={IncidentId}: {Code} - {Message}",
                            incidentId, ex.Code, ex.Message);
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds)));
        do
        {
            try
            {
                await PollDeploymentsOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Doctor monitoring poll iteration failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
