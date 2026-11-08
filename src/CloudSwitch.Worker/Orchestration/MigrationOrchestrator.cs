using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Recovery;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudSwitch.Worker.Orchestration;

public sealed class MigrationOrchestrator : IMigrationOrchestrator
{
    private readonly IInfrastructureExecutor _executor;
    private readonly ITargetHealthValidator _healthValidator;
    private readonly ITrafficRoutingService _routingService;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IJobLeaseStore? _leaseStore;
    private readonly ILogger<MigrationOrchestrator> _logger;

    public MigrationOrchestrator(
        IInfrastructureExecutor executor,
        ITargetHealthValidator healthValidator,
        ITrafficRoutingService routingService,
        IPublishEndpoint publishEndpoint,
        IJobLeaseStore? leaseStore = null,
        ILogger<MigrationOrchestrator>? logger = null)
    {
        _executor = executor;
        _healthValidator = healthValidator;
        _routingService = routingService;
        _publishEndpoint = publishEndpoint;
        _leaseStore = leaseStore;
        _logger = logger ?? NullLogger<MigrationOrchestrator>.Instance;
    }

    public Task<MigrationOutcome> ExecuteMigrationAsync(
        DeploymentRequested request,
        CancellationToken cancellationToken = default) =>
        ExecuteMigrationAsync(request, 1, cancellationToken);

    public async Task<MigrationOutcome> ExecuteMigrationAsync(
        DeploymentRequested request,
        int attempt,
        CancellationToken cancellationToken = default)
    {
        long sequence = 0;
        var currentStatus = JobStatus.Pending;
        var currentPhase = MigrationPhase.Pending;
        string? targetEndpoint = null;
        var spec = request.Specification;

        async Task PublishStatusAsync(
            JobStatus nextStatus,
            MigrationPhase phase,
            string? endpoint = null,
            string? errorCode = null,
            string? errorMessage = null)
        {
            sequence++;
            var previous = currentStatus;
            currentStatus = nextStatus;

            var statusEvent = new JobStatusChanged
            {
                EventId = Guid.NewGuid(),
                CorrelationId = request.CorrelationId,
                OccurredAt = DateTimeOffset.UtcNow,
                JobId = request.JobId,
                DeploymentId = request.DeploymentId,
                UserId = request.UserId,
                PreviousStatus = previous,
                Status = nextStatus,
                Phase = phase,
                Attempt = attempt,
                Sequence = sequence,
                TargetEndpoint = endpoint,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            };

            var publishToken = cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken;
            await _publishEndpoint.Publish(statusEvent, publishToken).ConfigureAwait(false);

            if (_leaseStore != null)
            {
                if (nextStatus == JobStatus.Running)
                {
                    await _leaseStore.UpdateCheckpointAsync(
                        request.JobId, attempt, phase, endpoint, publishToken).ConfigureAwait(false);
                }
                else
                {
                    await _leaseStore.MarkTerminalAsync(
                        request.JobId, attempt, nextStatus, errorCode, errorMessage, publishToken).ConfigureAwait(false);
                }
            }

            _logger.LogInformation(
                "Job {JobId} sequence {Sequence} attempt {Attempt}: {PreviousStatus} -> {Status} (Phase: {Phase})",
                request.JobId, sequence, attempt, previous, nextStatus, phase);
        }

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                const string cancelCode = "OPERATION_CANCELLED";
                const string cancelMsg = "Migration execution was cancelled before start.";
                await PublishStatusAsync(JobStatus.Failed, currentPhase, errorCode: cancelCode, errorMessage: cancelMsg).ConfigureAwait(false);
                return MigrationOutcome.Failed(currentPhase, cancelCode, cancelMsg, sequence);
            }

            // Validate target configuration presence
            if (spec.TargetConfigurationId == null || spec.TargetConfigurationId == Guid.Empty)
            {
                const string errCode = "MISSING_TARGET_CONFIGURATION";
                const string errMsg = "TargetConfigurationId must be provided to provision fallback cloud infrastructure.";
                await PublishStatusAsync(JobStatus.Failed, currentPhase, errorCode: errCode, errorMessage: errMsg).ConfigureAwait(false);
                return MigrationOutcome.Failed(currentPhase, errCode, errMsg, sequence);
            }

            // --- Phase 1: Provisioning ---
            currentPhase = MigrationPhase.Provisioning;
            await PublishStatusAsync(JobStatus.Running, currentPhase).ConfigureAwait(false);

            var provRequest = new ProvisioningRequest
            {
                JobId = request.JobId,
                DeploymentId = request.DeploymentId,
                UserId = request.UserId,
                Specification = spec,
                TargetConfigurationId = spec.TargetConfigurationId.Value,
                Provider = spec.Provider,
                TerraformModule = spec.TerraformModule ?? "container-vm-selectel:v1.0.0",
                Timeout = TimeSpan.FromMinutes(5),
                Attempt = attempt
            };

            var provResult = await _executor.ProvisionAsync(provRequest, cancellationToken).ConfigureAwait(false);
            if (!provResult.IsSuccess || string.IsNullOrEmpty(provResult.TargetEndpoint))
            {
                var errCode = provResult.ErrorCode ?? "PROVISIONING_FAILED";
                var errMsg = provResult.SanitizedOutput ?? "Infrastructure provisioning failed.";
                await PublishStatusAsync(JobStatus.Failed, currentPhase, errorCode: errCode, errorMessage: errMsg).ConfigureAwait(false);
                return MigrationOutcome.Failed(currentPhase, errCode, errMsg, sequence);
            }

            targetEndpoint = provResult.TargetEndpoint;

            if (cancellationToken.IsCancellationRequested)
            {
                const string cancelCode = "OPERATION_CANCELLED";
                const string cancelMsg = "Migration execution was cancelled after provisioning.";
                await PublishStatusAsync(JobStatus.Failed, currentPhase, targetEndpoint, cancelCode, cancelMsg).ConfigureAwait(false);
                return MigrationOutcome.Failed(currentPhase, cancelCode, cancelMsg, sequence, targetEndpoint);
            }

            // --- Phase 2: Target Health Validation ---
            currentPhase = MigrationPhase.TargetValidation;
            await PublishStatusAsync(JobStatus.Running, currentPhase, targetEndpoint).ConfigureAwait(false);

            var healthResult = await _healthValidator.ValidateAsync(targetEndpoint, spec.HealthCheck, cancellationToken).ConfigureAwait(false);
            if (!healthResult.IsHealthy)
            {
                const string errCode = "TARGET_HEALTH_CHECK_FAILED";
                var errMsg = $"Target endpoint {targetEndpoint} failed health check. Traffic cutover was prevented.";
                await PublishStatusAsync(JobStatus.Failed, currentPhase, targetEndpoint, errCode, errMsg).ConfigureAwait(false);
                return MigrationOutcome.Failed(currentPhase, errCode, errMsg, sequence, targetEndpoint);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                const string cancelCode = "OPERATION_CANCELLED";
                const string cancelMsg = "Migration execution was cancelled after health validation.";
                await PublishStatusAsync(JobStatus.Failed, currentPhase, targetEndpoint, cancelCode, cancelMsg).ConfigureAwait(false);
                return MigrationOutcome.Failed(currentPhase, cancelCode, cancelMsg, sequence, targetEndpoint);
            }

            // --- Phase 3: Traffic Cutover ---
            currentPhase = MigrationPhase.TrafficCutover;
            await PublishStatusAsync(JobStatus.Running, currentPhase, targetEndpoint).ConfigureAwait(false);

            var domain = spec.EntrypointDomain ?? $"{spec.Name}.cloudswitch.internal";
            var cutoverResult = await _routingService.CutoverAsync(domain, targetEndpoint, spec.TrafficRoutingProvider, cancellationToken).ConfigureAwait(false);
            if (!cutoverResult.IsSuccess)
            {
                var errCode = cutoverResult.ErrorCode ?? "TRAFFIC_CUTOVER_FAILED";
                var errMsg = cutoverResult.Message ?? "Traffic routing cutover failed.";
                await PublishStatusAsync(JobStatus.Failed, currentPhase, targetEndpoint, errCode, errMsg).ConfigureAwait(false);
                return MigrationOutcome.Failed(currentPhase, errCode, errMsg, sequence, targetEndpoint);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                const string cancelCode = "UNCERTAIN_CUTOVER_STATE";
                const string cancelMsg = "Migration was cancelled during/after traffic cutover. Verification pending.";
                await PublishStatusAsync(JobStatus.Failed, currentPhase, targetEndpoint, cancelCode, cancelMsg).ConfigureAwait(false);
                return MigrationOutcome.Failed(currentPhase, cancelCode, cancelMsg, sequence, targetEndpoint);
            }

            // --- Phase 4: Entrypoint Verification ---
            currentPhase = MigrationPhase.EntrypointVerification;
            await PublishStatusAsync(JobStatus.Running, currentPhase, targetEndpoint).ConfigureAwait(false);

            var healthPath = spec.HealthCheck?.Path ?? "/healthz";
            var verifyResult = await _routingService.VerifyEntrypointAsync(domain, healthPath, cancellationToken).ConfigureAwait(false);
            if (!verifyResult.IsSuccess)
            {
                var errCode = verifyResult.ErrorCode ?? "ENTRYPOINT_VERIFICATION_FAILED";
                var errMsg = verifyResult.Message ?? "Public entrypoint verification failed.";
                await PublishStatusAsync(JobStatus.Failed, currentPhase, targetEndpoint, errCode, errMsg).ConfigureAwait(false);
                return MigrationOutcome.Failed(currentPhase, errCode, errMsg, sequence, targetEndpoint);
            }

            // --- Phase 5: Completed ---
            currentPhase = MigrationPhase.Completed;
            var publicEntrypoint = $"https://{domain}";
            await PublishStatusAsync(JobStatus.Succeeded, currentPhase, targetEndpoint).ConfigureAwait(false);
            return MigrationOutcome.Succeeded(targetEndpoint, publicEntrypoint, sequence);
        }
        catch (OperationCanceledException)
        {
            const string cancelCode = "OPERATION_CANCELLED";
            const string cancelMsg = "Migration execution was cancelled.";
            await PublishStatusAsync(JobStatus.Failed, currentPhase, targetEndpoint, cancelCode, cancelMsg).ConfigureAwait(false);
            return MigrationOutcome.Failed(currentPhase, cancelCode, cancelMsg, sequence, targetEndpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing migration for JobId={JobId} at Phase={Phase}", request.JobId, currentPhase);
            const string errCode = "UNHANDLED_EXECUTION_ERROR";
            var errMsg = OutputSanitizer.Sanitize($"Unexpected error: {ex.Message}");
            await PublishStatusAsync(JobStatus.Failed, currentPhase, targetEndpoint, errCode, errMsg).ConfigureAwait(false);
            return MigrationOutcome.Failed(currentPhase, errCode, errMsg, sequence, targetEndpoint);
        }
    }
}
