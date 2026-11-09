using CloudSwitch.Contracts.Events;

namespace CloudSwitch.Worker.Orchestration;

public interface IMigrationOrchestrator
{
    Task<MigrationOutcome> ExecuteMigrationAsync(DeploymentRequested request, CancellationToken cancellationToken = default);
}
