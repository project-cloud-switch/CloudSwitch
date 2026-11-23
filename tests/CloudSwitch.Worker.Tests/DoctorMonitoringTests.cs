using CloudSwitch.Contracts.Events;
using CloudSwitch.Persistence.Domain;
using CloudSwitch.Worker.Doctor;
using CloudSwitch.Worker.Probing;
using Xunit;

namespace CloudSwitch.Worker.Tests;

public sealed class DoctorMonitoringTests
{
    [Fact]
    public void Consecutive_failure_threshold_transitions_to_unhealthy_and_opens_incident()
    {
        var deploymentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        const int failureThreshold = 3;

        // Sequence 1: 1st failure
        var obs1 = HealthObservation.Record(
            deploymentId: deploymentId,
            configurationRevision: 1,
            runtimeRevision: 1,
            sequence: 1,
            status: DeploymentHealthStatus.Healthy,
            incidentId: null,
            failures: 1,
            successes: 0,
            now: now);

        Assert.Equal(DeploymentHealthStatus.Healthy, obs1.Status);
        Assert.Equal(1, obs1.ConsecutiveFailures);
        Assert.Null(obs1.IncidentId);

        // Sequence 2: 2nd failure (still below threshold 3)
        obs1.Update(
            configurationRevision: 1,
            runtimeRevision: 1,
            sequence: 2,
            status: DeploymentHealthStatus.Healthy,
            incidentId: null,
            failures: 2,
            successes: 0,
            now: now.AddSeconds(5));

        Assert.Equal(DeploymentHealthStatus.Healthy, obs1.Status);
        Assert.Equal(2, obs1.ConsecutiveFailures);
        Assert.Null(obs1.IncidentId);

        // Sequence 3: 3rd failure (reaches failure threshold)
        var incident = Incident.Open(deploymentId, now.AddSeconds(10));
        obs1.Update(
            configurationRevision: 1,
            runtimeRevision: 1,
            sequence: 3,
            status: DeploymentHealthStatus.Unhealthy,
            incidentId: incident.Id,
            failures: failureThreshold,
            successes: 0,
            now: now.AddSeconds(10));

        Assert.Equal(DeploymentHealthStatus.Unhealthy, obs1.Status);
        Assert.Equal(failureThreshold, obs1.ConsecutiveFailures);
        Assert.Equal(incident.Id, obs1.IncidentId);
        Assert.Null(incident.ResolvedAt);
    }

    [Fact]
    public void Consecutive_success_threshold_transitions_to_healthy_and_resolves_incident()
    {
        var deploymentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        const int recoveryThreshold = 2;

        var incident = Incident.Open(deploymentId, now);
        var obs = HealthObservation.Record(
            deploymentId: deploymentId,
            configurationRevision: 1,
            runtimeRevision: 1,
            sequence: 5,
            status: DeploymentHealthStatus.Unhealthy,
            incidentId: incident.Id,
            failures: 4,
            successes: 0,
            now: now);

        // Probe 1 succeeds: successes = 1 (threshold is 2, so status stays Unhealthy)
        obs.Update(
            configurationRevision: 1,
            runtimeRevision: 1,
            sequence: 6,
            status: DeploymentHealthStatus.Unhealthy,
            incidentId: incident.Id,
            failures: 0,
            successes: 1,
            now: now.AddSeconds(5));

        Assert.Equal(DeploymentHealthStatus.Unhealthy, obs.Status);
        Assert.Equal(1, obs.ConsecutiveSuccesses);
        Assert.Null(incident.ResolvedAt);

        // Probe 2 succeeds: reaches recovery threshold -> status becomes Healthy, incident resolved
        incident.Resolve(now.AddSeconds(10));
        obs.Update(
            configurationRevision: 1,
            runtimeRevision: 1,
            sequence: 7,
            status: DeploymentHealthStatus.Healthy,
            incidentId: null,
            failures: 0,
            successes: 2,
            now: now.AddSeconds(10));

        Assert.Equal(DeploymentHealthStatus.Healthy, obs.Status);
        Assert.Equal(recoveryThreshold, obs.ConsecutiveSuccesses);
        Assert.Null(obs.IncidentId);
        Assert.NotNull(incident.ResolvedAt);
        Assert.Equal(now.AddSeconds(10), incident.ResolvedAt);
    }

    [Fact]
    public void Resolving_already_resolved_incident_throws_domain_exception()
    {
        var incident = Incident.Open(Guid.NewGuid(), DateTimeOffset.UtcNow);
        incident.Resolve(DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => incident.Resolve(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Health_observation_validation_guards_require_incident_when_unhealthy()
    {
        Assert.Throws<DomainException>(() => HealthObservation.Record(
            deploymentId: Guid.NewGuid(),
            configurationRevision: 1,
            runtimeRevision: 1,
            sequence: 1,
            status: DeploymentHealthStatus.Unhealthy,
            incidentId: null, // Invalid: Unhealthy status requires incident!
            failures: 3,
            successes: 0,
            now: DateTimeOffset.UtcNow));
    }
}
