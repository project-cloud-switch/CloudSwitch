using CloudSwitch.Contracts.Events;

namespace CloudSwitch.Worker.Orchestration;

/// <summary>
/// Orchestrates the 4-phase reliable migration workflow:
/// Provisioning -> Target Validation -> Traffic Cutover -> Entrypoint Verification.
/// Emits JobStatusChanged events with strictly monotonic sequence numbers through the outbox.
/// </summary>
public interface IMigrationOrchestrator
{
    Task<MigrationOutcome> ExecuteMigrationAsync(
        DeploymentRequested request,
        CancellationToken cancellationToken = default) =>
        ExecuteMigrationAsync(request, 1, cancellationToken);

    Task<MigrationOutcome> ExecuteMigrationAsync(
        DeploymentRequested request,
        int attempt,
        CancellationToken cancellationToken = default);
}
