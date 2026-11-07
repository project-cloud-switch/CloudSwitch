namespace CloudSwitch.Worker.Infrastructure;

/// <summary>
/// Executes infrastructure provisioning and state reconciliation via Terraform
/// on the selected target fallback cloud provider.
/// </summary>
public interface IInfrastructureExecutor
{
    /// <summary>
    /// Provisions target container resources using approved Terraform modules and inputs.
    /// Retries for the same target deployment converge on existing target state.
    /// </summary>
    Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles actual target infrastructure and state identity before resuming interrupted jobs.
    /// </summary>
    Task<ReconciliationResult> ReconcileAsync(ReconciliationRequest request, CancellationToken cancellationToken = default);
}
