using CloudSwitch.Contracts.Events;
using CloudSwitch.Persistence.Diagnostics;
using CloudSwitch.Worker.Orchestration;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudSwitch.Worker.Consumers;

public sealed class SafeConsumerFailureException(string category)
    : Exception($"Migration job execution failed: {category}.")
{
    public string Category { get; } = category;
}

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
            "Consuming DeploymentRequested: job={JobId} deployment={DeploymentId} correlation={CorrelationId} trigger={Trigger}",
            request.JobId, request.DeploymentId, request.CorrelationId, request.Trigger);

        try
        {
            var outcome = await _orchestrator.ExecuteMigrationAsync(request, context.CancellationToken).ConfigureAwait(false);

            if (outcome.ErrorCode is "JOB_NOT_ACQUIRED" or "LEASE_LOST")
            {
                _logger.LogInformation(
                    "Skipped duplicate or stale execution: job={JobId} correlation={CorrelationId}",
                    request.JobId, request.CorrelationId);
            }
            else if (outcome.IsSuccess)
            {
                _logger.LogInformation(
                    "Migration succeeded: job={JobId} correlation={CorrelationId} target={TargetEndpoint} entrypoint={PublicEntrypoint}",
                    request.JobId, request.CorrelationId, outcome.TargetEndpoint, outcome.PublicEntrypoint);
            }
            else
            {
                _logger.LogWarning(
                    "Migration failed: job={JobId} deployment={DeploymentId} correlation={CorrelationId} phase={Phase} code={ErrorCode}",
                    request.JobId, request.DeploymentId, request.CorrelationId, outcome.FinalPhase, outcome.ErrorCode);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SafeConsumerFailureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var category = SafeDiagnostics.Categorize(exception);
            _logger.LogError(
                "Job execution threw: job={JobId} deployment={DeploymentId} correlation={CorrelationId} category={Category} details={Details}",
                request.JobId, request.DeploymentId, request.CorrelationId, category, SafeDiagnostics.Describe(exception));
            throw new SafeConsumerFailureException(category);
        }
    }
}