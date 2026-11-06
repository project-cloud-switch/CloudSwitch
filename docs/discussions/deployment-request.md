# Discussion: DeploymentRequested

**Status: agreed.** Reviewers: Polina and Vlad. Related task: [#2](https://github.com/project-cloud-switch/CloudSwitch/issues/2).

## Current gap

API and Doctor publish the same request. The previous specification had only name, image, environment and provider; it could not describe the agreed migration or run autonomous recovery without accessing the failed source.

## Agreed decisions

1. **Trigger and incident tracking:**
   - Added `DeploymentTrigger` (`Manual`, `Automatic`) to `DeploymentRequested`.
   - Added `IncidentId` (`Guid?`) for automatic recovery to enable database-level and execution deduplication per `(DeploymentId, IncidentId)`.

2. **Immutable snapshot content (`DeploymentSpecification`):**
   - **Container & Registry:** `ContainerPort` (default 80), `ImagePullSecretReference` (link to registry token in Vault), pinned image with digest/tag.
   - **Resources:** `CpuCores` (default 1.0) and `MemoryMb` (default 512).
   - **Configuration & Secrets:** Non-secret `Env` dictionary, and `SecretReferences` dictionary containing Vault references (`vault://path#key`). Raw secrets are **never** embedded in broker messages or committed logs.
   - **Infrastructure & Execution:** `TargetConfigurationId` (PostgreSQL profile reference) and approved `TerraformModule` name and version/revision.
   - **Verification & Cutover:** `HealthCheck` (`HealthCheckPolicy`) for pre-cutover validation, `EntrypointDomain` for traffic switching, and `TrafficRoutingProvider`.

3. **Autonomy guarantee:**
   - The worker executes provisioning and validation completely out-of-band using this snapshot. No runtime calls to the failed source cloud are permitted.

4. **Implementation status:**
   - C# contracts in `CloudSwitch.Contracts` and unit tests in `CloudSwitch.Contracts.Tests` have been implemented and verified.

