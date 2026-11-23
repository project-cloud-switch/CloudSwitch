using CloudSwitch.Contracts;
using CloudSwitch.Contracts.Events;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Recovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudSwitch.Worker.Orchestration;

public sealed class MigrationOrchestrator(
    IInfrastructureExecutor executor,
    ITargetHealthValidator healthValidator,
    ITrafficRoutingService routing,
    IJobLeaseStore store,
    IOptions<WorkerOptions> options,
    ILogger<MigrationOrchestrator> logger) : IMigrationOrchestrator
{
    private readonly string workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public async Task<MigrationOutcome> ExecuteMigrationAsync(DeploymentRequested request, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var duration = TimeSpan.FromSeconds(settings.LeaseSeconds);
        var lease = await store.TryAcquireAsync(request, workerId, duration, settings.MaxAttempts, cancellationToken);
        if (lease is null)
            return MigrationOutcome.Failed(MigrationPhase.Pending, "JOB_NOT_ACQUIRED", "The job is already leased or terminal.", 0);

        request = lease.Request;

        using var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(settings.OverallTimeoutSeconds));
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, overallCts.Token);
        using var stopHeartbeat = new CancellationTokenSource();
        var leaseLostFlag = false;
        var heartbeat = RenewLeaseAsync(lease, duration, execution, stopHeartbeat.Token, () => leaseLostFlag = true);

        var phase = lease.Phase;
        var cutover = lease.Cutover;
        var endpoint = lease.TargetEndpoint;
        var sequence = lease.Sequence;
        var fingerprint = lease.StateFingerprint;

        async Task RecordAsync(JobStatus status, string? code = null, string? message = null)
        {
            JobStatusChanged? progress;
            try
            {
                progress = await store.RecordProgressAsync(lease, status, phase, cutover, endpoint, code, message, fingerprint, execution.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw new ProgressPersistenceException(error); }

            if (progress is null)
                throw new LeaseLostException();

            sequence = progress.Sequence;
        }

        async Task<MigrationOutcome> FailAsync(string code, string message)
        {
            if (cutover is CutoverOutcome.Applied or CutoverOutcome.Uncertain)
            {
                phase = MigrationPhase.TrafficCutover;
                cutover = CutoverOutcome.Uncertain;
            }
            await RecordAsync(JobStatus.Failed, code, message);
            return MigrationOutcome.Failed(phase, code, message, sequence, endpoint?.ToString());
        }

        try
        {
            execution.Token.ThrowIfCancellationRequested();
            var specification = request.Specification;
            if (specification.TargetConfigurationId is null || specification.TargetConfigurationId == Guid.Empty
                || string.IsNullOrWhiteSpace(specification.TerraformModule) || specification.HealthCheck is null
                || string.IsNullOrWhiteSpace(specification.EntrypointDomain) || string.IsNullOrWhiteSpace(specification.TrafficRoutingProvider))
                return await FailAsync("MISSING_MIGRATION_SNAPSHOT", "A complete migration snapshot is required.");

            if (settings.Simulation && (specification.Provider != "local" || specification.TerraformModule != "local-stateless-v1:local-simulation-v1"))
                return await FailAsync("SIMULATION_TARGET_REQUIRED", "Simulation accepts only the approved local target module.");

            if (cutover is CutoverOutcome.Applied or CutoverOutcome.Uncertain)
                return await FailAsync("UNCERTAIN_CUTOVER_STATE", "Interrupted traffic cutover requires manual reconciliation.");

            // =========================================================================
            // BOUNDARY 1: Infrastructure Provisioning & State Reconciliation
            // =========================================================================
            phase = MigrationPhase.Provisioning;
            await RecordAsync(JobStatus.Running);

            using (var phaseCts = new CancellationTokenSource(TimeSpan.FromSeconds(settings.PhaseTimeoutSeconds)))
            using (var stepLinked = CancellationTokenSource.CreateLinkedTokenSource(execution.Token, phaseCts.Token))
            {
                if (lease.Attempt > 1)
                {
                    var reconciled = await executor.ReconcileAsync(new ReconciliationRequest
                    {
                        DeploymentId = request.DeploymentId,
                        Provider = specification.Provider,
                        TerraformModule = specification.TerraformModule,
                        ExpectedTargetEndpoint = endpoint?.ToString(),
                        StateFingerprint = fingerprint,
                        Specification = specification,
                        TargetConfigurationId = specification.TargetConfigurationId,
                        FencingToken = lease.FencingToken
                    }, stepLinked.Token);

                    if (reconciled.ErrorCode is not null)
                        return await FailAsync("CONFLICTING_TARGET_STATE", "Target state requires manual reconciliation.");

                    endpoint = reconciled.Exists ? SafeEndpoint(reconciled.ObservedTargetEndpoint) : null;
                    fingerprint = reconciled.Exists ? reconciled.StateFingerprint : null;
                }

                if (endpoint is null)
                {
                    var provisioned = await executor.ProvisionAsync(new ProvisioningRequest
                    {
                        JobId = request.JobId,
                        DeploymentId = request.DeploymentId,
                        UserId = request.UserId,
                        Specification = request.Specification,
                        TargetConfigurationId = specification.TargetConfigurationId.Value,
                        Provider = specification.Provider,
                        TerraformModule = specification.TerraformModule,
                        Attempt = lease.Attempt,
                        FencingToken = lease.FencingToken
                    }, stepLinked.Token);

                    if (!provisioned.IsSuccess)
                        return await FailAsync("PROVISIONING_FAILED", "Target provisioning failed.");

                    endpoint = SafeEndpoint(provisioned.TargetEndpoint);
                    fingerprint = provisioned.StateFingerprint;
                }
            }

            // =========================================================================
            // BOUNDARY 2: Target Health Validation
            // =========================================================================
            phase = MigrationPhase.TargetValidation;
            await RecordAsync(JobStatus.Running);

            using (var phaseCts = new CancellationTokenSource(TimeSpan.FromSeconds(settings.PhaseTimeoutSeconds)))
            using (var stepLinked = CancellationTokenSource.CreateLinkedTokenSource(execution.Token, phaseCts.Token))
            {
                var health = await healthValidator.ValidateAsync(endpoint.ToString(), specification.HealthCheck, stepLinked.Token);
                if (!health.IsHealthy)
                    return await FailAsync("TARGET_HEALTH_CHECK_FAILED", "Target health validation failed. Traffic cutover was prevented.");
            }

            // =========================================================================
            // BOUNDARY 3: Traffic Cutover & Routing State Reconciliation
            // =========================================================================
            phase = MigrationPhase.TrafficCutover;

            using (var phaseCts = new CancellationTokenSource(TimeSpan.FromSeconds(settings.PhaseTimeoutSeconds)))
            using (var stepLinked = CancellationTokenSource.CreateLinkedTokenSource(execution.Token, phaseCts.Token))
            {
                // Inspect routing state to detect whether cutover already converged or is in conflict
                var inspected = await routing.InspectAsync(
                    specification.EntrypointDomain,
                    endpoint.ToString(),
                    specification.TrafficRoutingProvider,
                    stepLinked.Token);

                if (inspected.IsConflict)
                {
                    cutover = CutoverOutcome.Uncertain;
                    return await FailAsync("CONFLICTING_ROUTING_STATE",
                        $"Routing conflict detected for domain {specification.EntrypointDomain}: {inspected.ErrorMessage}");
                }

                if (inspected.MatchesExpectedTarget)
                {
                    // CONVERGED: External DNS already points to verified target endpoint
                    logger.LogInformation("Traffic routing already converged for {Domain} -> {Target}.",
                        specification.EntrypointDomain, endpoint);
                    cutover = CutoverOutcome.Applied;
                }
                else
                {
                    // SAFE RESUME: Perform traffic cutover mutation
                    cutover = CutoverOutcome.Uncertain;
                    await RecordAsync(JobStatus.Running);

                    var switched = await routing.CutoverAsync(
                        specification.EntrypointDomain,
                        endpoint.ToString(),
                        specification.TrafficRoutingProvider,
                        stepLinked.Token);

                    if (!switched.IsSuccess)
                        return await FailAsync("TRAFFIC_CUTOVER_FAILED", "Traffic cutover requires manual reconciliation.");

                    cutover = CutoverOutcome.Applied;
                }
            }

            // =========================================================================
            // BOUNDARY 4: Public Entrypoint Verification
            // =========================================================================
            phase = MigrationPhase.EntrypointVerification;
            await RecordAsync(JobStatus.Running);

            using (var phaseCts = new CancellationTokenSource(TimeSpan.FromSeconds(settings.PhaseTimeoutSeconds)))
            using (var stepLinked = CancellationTokenSource.CreateLinkedTokenSource(execution.Token, phaseCts.Token))
            {
                var verified = await routing.VerifyEntrypointAsync(
                    specification.EntrypointDomain,
                    specification.HealthCheck.Path,
                    stepLinked.Token);

                if (!verified.IsSuccess)
                    return await FailAsync("ENTRYPOINT_VERIFICATION_FAILED", "Public entrypoint verification failed.");
            }

            // Succeeded: complete migration!
            phase = MigrationPhase.Completed;
            cutover = CutoverOutcome.Verified;
            await RecordAsync(JobStatus.Succeeded);
            return MigrationOutcome.Succeeded(endpoint.ToString(), $"https://{specification.EntrypointDomain}", sequence);
        }
        catch (ProgressPersistenceException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown: do NOT mark terminal failure in DB; let lease expire so another worker can recover
            logger.LogInformation("Host shutdown requested; stopping execution for JobId={JobId}.", request.JobId);
            throw;
        }
        catch (OperationCanceledException) when (overallCts.IsCancellationRequested)
        {
            logger.LogWarning("Overall execution timeout exceeded for JobId={JobId}.", request.JobId);
            return await FailAsync("EXECUTION_TIMEOUT", $"Overall execution exceeded deadline of {settings.OverallTimeoutSeconds}s.");
        }
        catch (Exception error) when (error is LeaseLostException || leaseLostFlag)
        {
            logger.LogWarning("Execution stopped after losing the lease for JobId={JobId}.", request.JobId);
            return MigrationOutcome.Failed(phase, "LEASE_LOST", "Job ownership changed; execution stopped.", sequence, endpoint?.ToString());
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            if (phase == MigrationPhase.TrafficCutover)
            {
                return await FailAsync("CUTOVER_TIMEOUT",
                    $"Traffic cutover timed out after {settings.PhaseTimeoutSeconds}s. State is uncertain; reconciliation required.");
            }
            return await FailAsync("PHASE_TIMEOUT", $"Phase {phase} timed out after {settings.PhaseTimeoutSeconds}s.");
        }
        catch (Exception)
        {
            logger.LogError("Unexpected migration failure for JobId={JobId} at Phase={Phase}.", request.JobId, phase);
            return await FailAsync("UNHANDLED_EXECUTION_ERROR", "An unexpected execution failure occurred. Diagnostics were redacted.");
        }
        finally
        {
            await stopHeartbeat.CancelAsync();
            await heartbeat;
        }
    }

    private async Task RenewLeaseAsync(
        JobLease lease,
        TimeSpan duration,
        CancellationTokenSource execution,
        CancellationToken stoppingToken,
        Action onLeaseLost)
    {
        using var timer = new PeriodicTimer(duration / 3);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!await store.RenewAsync(lease, duration, stoppingToken))
                {
                    onLeaseLost();
                    await execution.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception)
        {
            logger.LogWarning("Lease renewal failed for JobId={JobId}; execution is stopping.", lease.JobId);
            onLeaseLost();
            await execution.CancelAsync();
        }
    }

    private static Uri SafeEndpoint(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "http" or "https" && string.IsNullOrEmpty(endpoint.UserInfo)
            && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment)
                ? endpoint : throw new InvalidDataException("The infrastructure adapter returned an invalid endpoint.");

    private sealed class LeaseLostException : Exception;
    private sealed class ProgressPersistenceException(Exception inner) : Exception("Job progress could not be persisted.", inner);
}
