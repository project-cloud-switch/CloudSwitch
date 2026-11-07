using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Worker.Infrastructure;
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
    private readonly ILogger<MigrationOrchestrator> _logger;

    public MigrationOrchestrator(
        IInfrastructureExecutor executor,
        ITargetHealthValidator healthValidator,
        ITrafficRoutingService routingService,
        IPublishEndpoint publishEndpoint,
        ILogger<MigrationOrchestrator>? logger = null)
    {
        _executor = executor;
        _healthValidator = healthValidator;
        _routingService = routingService;
        _publishEndpoint = publishEndpoint;
        _logger = logger ?? NullLogger<MigrationOrchestrator>.Instance;
    }

    public async Task<MigrationOutcome> ExecuteMigrationAsync(
        DeploymentRequested request,
        CancellationToken cancellationToken = default)
    {
        long sequence = 0;
        var currentStatus = JobStatus.Pending;
        var spec = request.Specification;

        async Task PublishStatusAsync(
            JobStatus nextStatus,
            MigrationPhase phase,
            string? targetEndpoint = null,
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
                Attempt = 1,
                Sequence = sequence,
                TargetEndpoint = targetEndpoint,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            };

            await _publishEndpoint.Publish(statusEvent, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Job {JobId} sequence {Sequence}: {PreviousStatus} -> {Status} (Phase: {Phase})",
                request.JobId, sequence, previous, nextStatus, phase);
        }

        // --- Phase 1: Provisioning ---
        await PublishStatusAsync(JobStatus.Running, MigrationPhase.Provisioning).ConfigureAwait(false);

        var provRequest = new ProvisioningRequest
        {
            JobId = request.JobId,
            DeploymentId = request.DeploymentId,
            UserId = request.UserId,
            Specification = spec,
            TargetConfigurationId = spec.TargetConfigurationId ?? Guid.Empty,
            Provider = spec.Provider,
            TerraformModule = spec.TerraformModule ?? "container-vm-selectel:v1.0.0",
            Timeout = TimeSpan.FromMinutes(5)
        };

        var provResult = await _executor.ProvisionAsync(provRequest, cancellationToken).ConfigureAwait(false);
        if (!provResult.IsSuccess || string.IsNullOrEmpty(provResult.TargetEndpoint))
        {
            var errCode = provResult.ErrorCode ?? "PROVISIONING_FAILED";
            var errMsg = provResult.SanitizedOutput ?? "Infrastructure provisioning failed.";
            await PublishStatusAsync(JobStatus.Failed, MigrationPhase.Provisioning, errorCode: errCode, errorMessage: errMsg).ConfigureAwait(false);
            return MigrationOutcome.Failed(MigrationPhase.Provisioning, errCode, errMsg, sequence);
        }

        var targetEndpoint = provResult.TargetEndpoint;

        // --- Phase 2: Target Health Validation ---
        await PublishStatusAsync(JobStatus.Running, MigrationPhase.TargetValidation, targetEndpoint).ConfigureAwait(false);

        var healthResult = await _healthValidator.ValidateAsync(targetEndpoint, spec.HealthCheck, cancellationToken).ConfigureAwait(false);
        if (!healthResult.IsHealthy)
        {
            const string errCode = "TARGET_HEALTH_CHECK_FAILED";
            var errMsg = $"Target endpoint {targetEndpoint} failed health check. Traffic cutover was prevented.";
            await PublishStatusAsync(JobStatus.Failed, MigrationPhase.TargetValidation, targetEndpoint, errCode, errMsg).ConfigureAwait(false);
            return MigrationOutcome.Failed(MigrationPhase.TargetValidation, errCode, errMsg, sequence, targetEndpoint);
        }

        // --- Phase 3: Traffic Cutover ---
        await PublishStatusAsync(JobStatus.Running, MigrationPhase.TrafficCutover, targetEndpoint).ConfigureAwait(false);

        var domain = spec.EntrypointDomain ?? $"{spec.Name}.cloudswitch.internal";
        var cutoverResult = await _routingService.CutoverAsync(domain, targetEndpoint, spec.TrafficRoutingProvider, cancellationToken).ConfigureAwait(false);
        if (!cutoverResult.IsSuccess)
        {
            var errCode = cutoverResult.ErrorCode ?? "TRAFFIC_CUTOVER_FAILED";
            var errMsg = cutoverResult.Message ?? "Traffic routing cutover failed.";
            await PublishStatusAsync(JobStatus.Failed, MigrationPhase.TrafficCutover, targetEndpoint, errCode, errMsg).ConfigureAwait(false);
            return MigrationOutcome.Failed(MigrationPhase.TrafficCutover, errCode, errMsg, sequence, targetEndpoint);
        }

        // --- Phase 4: Entrypoint Verification ---
        await PublishStatusAsync(JobStatus.Running, MigrationPhase.EntrypointVerification, targetEndpoint).ConfigureAwait(false);

        var healthPath = spec.HealthCheck?.Path ?? "/healthz";
        var verifyResult = await _routingService.VerifyEntrypointAsync(domain, healthPath, cancellationToken).ConfigureAwait(false);
        if (!verifyResult.IsSuccess)
        {
            var errCode = verifyResult.ErrorCode ?? "ENTRYPOINT_VERIFICATION_FAILED";
            var errMsg = verifyResult.Message ?? "Public entrypoint verification failed.";
            await PublishStatusAsync(JobStatus.Failed, MigrationPhase.EntrypointVerification, targetEndpoint, errCode, errMsg).ConfigureAwait(false);
            return MigrationOutcome.Failed(MigrationPhase.EntrypointVerification, errCode, errMsg, sequence, targetEndpoint);
        }

        // --- Phase 5: Completed ---
        var publicEntrypoint = $"https://{domain}";
        await PublishStatusAsync(JobStatus.Succeeded, MigrationPhase.Completed, targetEndpoint).ConfigureAwait(false);
        return MigrationOutcome.Succeeded(targetEndpoint, publicEntrypoint, sequence);
    }
}
