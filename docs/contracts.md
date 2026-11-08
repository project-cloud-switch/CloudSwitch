# Service contracts

`CloudSwitch.Contracts` holds shared message definitions. Service logic and persistence stay in API, Worker and Notifications.

Remaining decisions and the meeting agenda are in [contract readiness discussions](discussions/README.md).

## Implemented contracts

| Message | Route | Payload |
| --- | --- | --- |
| `DeploymentRequested` | API or Doctor → Deployer | Job identity and deployment snapshot. |
| `JobStatusChanged` | Deployer → Notifications | Previous/current status, attempt, sequence and optional safe error. |
| `DeploymentHealthChanged` | Doctor → Notifications | Provider, health transition, incident ID, sequence and safe reason. |

Doctor and Deployer run inside Worker. See the [architecture diagram](architecture.md).

Job events carry `EventId`, `CorrelationId`, `OccurredAt`, `JobId`, `DeploymentId` and `UserId`. The current snapshot contains only `Name`, `ContainerImage`, `Environment` and `Provider`. Statuses are `Pending`, `Running`, `Succeeded` and `Failed`, serialized as strings.

## Health monitoring and recovery

`DeploymentMonitoringConfiguration` identifies the deployment, owner, provider and endpoint. `HealthCheckPolicy` defines the HTTP path, interval/timeout in seconds and consecutive failure/recovery thresholds. Services validate positive values, allowed HTTP(S) destinations and redirects, and successful application responses.

`RecoveryPolicy` defaults to `NotifyOnly`. `Automatic` requires an approved `TargetConfigurationId`; credentials stay outside the policy.

Services enforce nonempty identities, valid enum values and a positive sequence. An `Unhealthy` event requires an incident ID. C# `required` checks field presence, not these invariants.

Doctor publishes `DeploymentHealthChanged` on persisted transitions between `Unknown`, `Healthy` and `Unhealthy`, not on every probe. Confirm failure only after the configured threshold. Assign one `IncidentId` per outage episode, retain it until recovery is confirmed, and include it on the recovery event. Sequence increases per deployment across provider changes. Unknown never triggers migration. A failed application probe does not prove a provider-wide outage.

- Notifications alerts the owner on a confirmed failure in either mode.
- Doctor saves one migration job per incident and publishes `DeploymentRequested` in `Automatic`; `NotifyOnly` creates no automatic job.
- Before acting, Doctor checks current policy, active provider, health sequence/incident and existing jobs. A stale observation must not migrate an already-recovered or already-migrated deployment.

Doctor makes this decision directly; Worker does not consume `DeploymentHealthChanged` to create another request. API and Doctor share the same durable job/request rules.

API stores monitoring settings in shared PostgreSQL and reads health/job progress directly. An event such as `DeploymentMonitoringConfigurationChanged` is proposed for configuration updates, but its schema and replica delivery are [under discussion](discussions/doctor-configuration.md). It is not implemented or part of the agreed queue topology.

Monitoring implementations remain planned in [#14](https://github.com/project-cloud-switch/CloudSwitch/issues/14).

## Migration contracts — agreed and implemented in [#2](https://github.com/project-cloud-switch/CloudSwitch/issues/2)

The migration request and progress contracts are finalized per [deployment-request discussion](discussions/deployment-request.md) and [job-progress discussion](discussions/job-progress.md):

- `DeploymentRequested`: common request published by API and Doctor; carries `Trigger` (`Manual` or `Automatic`), optional `IncidentId` for deduplication, and the immutable `DeploymentSpecification` snapshot.
- `DeploymentSpecification`:
  - Pinned container image (with tag or sha256 digest), `ContainerPort`, `ImagePullSecretReference` (Vault reference).
  - Compute resources: `CpuCores` and `MemoryMb`.
  - Non-secret `Env` dictionary and `SecretReferences` dictionary (`vault://path#key`). Zero plaintext credentials in broker messages or logs.
  - Target cloud infrastructure: `TargetConfigurationId` (PostgreSQL reference) and approved `TerraformModule`.
  - Verification & cutover: `HealthCheck` (`HealthCheckPolicy`), `EntrypointDomain`, and `TrafficRoutingProvider`.
- `JobStatusChanged`: carries strictly monotonic per-job `Sequence`, `Attempt`, `PreviousStatus`, `Status`, `Phase` (`MigrationPhase`: `Pending`, `Provisioning`, `TargetValidation`, `TrafficCutover`, `EntrypointVerification`, `Completed`), direct `TargetEndpoint`, and sanitized `ErrorCode` / `ErrorMessage`.
- Out-of-band autonomy: Worker executes provisioning and verification entirely from this snapshot; runtime calls to the failed source cloud are prohibited.

## Messaging rules

Use MassTransit JSON messaging. Keep correlation IDs stable; preserve event IDs on redelivery. Worker deduplicates jobs by `JobId`; status consumers use `EventId` and the per-job `Sequence` to avoid duplicate or stale updates. Persist state and outgoing events together through an outbox.

Credentials are referenced, never embedded. Services validate inputs and ownership. Coordinate breaking schema or message-identity changes; keep JSON fixtures as compatibility tests.

## RabbitMQ baseline

| Message exchange | Receive queue |
| --- | --- |
| `CloudSwitch.Contracts.Events:DeploymentRequested` | `cloudswitch-worker-deployment-requested` |
| `CloudSwitch.Contracts.Events:JobStatusChanged` | `cloudswitch-notifications-job-status-changed` |
| `CloudSwitch.Contracts.Events:DeploymentHealthChanged` | `cloudswitch-notifications-deployment-health-changed` |

Deployer replicas share the request queue. Notifications has separate health/status queues; it does not compete with Deployer for requests. Create bindings before publishing; configure bounded retries and error queues in each service.

Current tests verify JSON compatibility, message identities, delivery and correlation using MassTransit 8.5.8 in memory. Broker provisioning and the complete migration remain service integration work.
