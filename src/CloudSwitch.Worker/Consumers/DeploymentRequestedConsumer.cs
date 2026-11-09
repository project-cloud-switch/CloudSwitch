using CloudSwitch.Contracts.Events;
using CloudSwitch.Worker.Orchestration;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudSwitch.Worker.Consumers;

/// <summary>
/// MassTransit consumer for DeploymentRequested messages. Orchestrates target provisioning,
/// health validation, traffic cutover, and entrypoint verification.
/// </summary>
public sealed class DeploymentRequestedConsumer : IConsumer<DeploymentRequested>
{
    private readonly IMigrationOrchestrator _orchestrator;
    private readonly ILogger<DeploymentRequestedConsumer> _logger;

    public DeploymentRequestedConsumer(
        IMigrationOrchestrator orchestrator,
        ILogger<DeploymentRequestedConsumer>? logger = null)
    {
        _orchestrator = orchestrator;
        _logger = logger ?? NullLogger<DeploymentRequestedConsumer>.Instance;
    }

    public async Task Consume(ConsumeContext<DeploymentRequested> context)
    {
        var request = context.Message;
        _logger.LogInformation(
            "Consuming DeploymentRequested for JobId={JobId}, DeploymentId={DeploymentId}, Trigger={Trigger}",
            request.JobId, request.DeploymentId, request.Trigger);

        var outcome = await _orchestrator.ExecuteMigrationAsync(request, context.CancellationToken).ConfigureAwait(false);

        if (outcome.ErrorCode is "JOB_NOT_ACQUIRED" or "LEASE_LOST")
        {
            _logger.LogInformation("Skipped duplicate or stale execution for JobId={JobId}.", request.JobId);
        }
        else if (outcome.IsSuccess)
        {
            _logger.LogInformation(
                "Migration Succeeded for JobId={JobId}. Target={TargetEndpoint}, Entrypoint={PublicEntrypoint}",
                request.JobId, outcome.TargetEndpoint, outcome.PublicEntrypoint);
        }
        else
        {
            _logger.LogWarning(
                "Migration Failed for JobId={JobId} at Phase={Phase} with Code={ErrorCode}: {ErrorMessage}",
                request.JobId, outcome.FinalPhase, outcome.ErrorCode, outcome.ErrorMessage);
        }
    }
}
