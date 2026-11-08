# Service contracts

`CloudSwitch.Contracts` holds shared message definitions. Service logic and persistence stay in API, Worker and Notifications.

The [API v1 decisions](decisions/api-v1.md) establish the implemented schema and durable submission baseline. The [discussion notes](discussions/README.md) retain the original questions and service-integration follow-ups.

## Implemented contracts

| Message | Route | Payload |
| --- | --- | --- |
| `DeploymentRequested` | API or Doctor → Deployer | Job identity and deployment snapshot. |
| `JobStatusChanged` | Deployer → Notifications | Previous/current status, attempt, sequence and optional safe error. |
| `DeploymentHealthChanged` | Doctor → Notifications | Provider, health transition, incident ID, sequence and safe reason. |

Doctor and Deployer run inside Worker. See the [architecture diagram](architecture.md).

Job events carry `EventId`, `CorrelationId`, `OccurredAt`, `JobId`, `DeploymentId` and `UserId`. `DeploymentSpecification` retains `Name`, `ContainerImage`, `Environment` and target `Provider`, and adds a complete `Migration` snapshot. `DeploymentRequested` adds `Trigger` (Manual by default) and optional `IncidentId`. Statuses remain `Pending`, `Running`, `Succeeded` and `Failed`, serialized as strings.

## Health monitoring and recovery

`DeploymentMonitoringConfiguration` identifies the deployment, owner, provider and endpoint. `HealthCheckPolicy` defines the HTTP path, interval/timeout in seconds and consecutive failure/recovery thresholds. Services validate positive values, allowed HTTP(S) destinations and redirects, and successful application responses.

`RecoveryPolicy` defaults to `NotifyOnly`. `Automatic` requires an approved `TargetConfigurationId`; credentials stay outside the policy.

Services enforce nonempty identities, valid enum values and a positive sequence. An `Unhealthy` event requires an incident ID. C# `required` checks field presence, not these invariants.

Doctor publishes `DeploymentHealthChanged` on persisted transitions between `Unknown`, `Healthy` and `Unhealthy`, not on every probe. Confirm failure only after the configured threshold. Assign one `IncidentId` per outage episode, retain it until recovery is confirmed, and include it on the recovery event. Sequence increases per deployment across provider changes. Unknown never triggers migration. A failed application probe does not prove a provider-wide outage.

- Notifications alerts the owner on a confirmed failure in either mode.
- Doctor saves one migration job per incident and publishes `DeploymentRequested` in `Automatic`; `NotifyOnly` creates no automatic job.
- Before acting, Doctor checks current policy, active provider, health sequence/incident and existing jobs. A stale observation must not migrate an already-recovered or already-migrated deployment.

Doctor makes this decision directly; Worker does not consume `DeploymentHealthChanged` to create another request. API and Doctor share the same durable job/request rules.

API stores monitoring settings in shared PostgreSQL and reads health/job progress directly. Initial Doctor configuration discovery reads active rows on startup and polls revisions. `DeploymentMonitoringConfigurationChanged` and replica distribution are deferred; no configuration event is part of the current queue topology.

Monitoring execution remains planned in [#14](https://github.com/project-cloud-switch/CloudSwitch/issues/14). Shared job creation already enforces current health, policy, incident and revision checks for automatic submissions.

## Migration snapshot — implemented API baseline

`DeploymentSpecification.Migration` captures:

- `ConfigurationRevision` and `Source`: active provider, endpoint and runtime revision (zero means the registered source before Worker initialization).
- `Target`: target ID/revision, provider, approved module ID/source/revision, allowed inputs and secret references.
- `Application`: digest-pinned image, port, CPU millicores, memory megabytes, nonsecret settings and secret references.
- `TargetHealthCheck` and `Traffic`: health policy and traffic driver/record/zone/credential reference.

API and Doctor use `JobSubmissionService` from `CloudSwitch.Persistence` to save this snapshot and its outgoing request atomically. The manual/automatic trigger is explicit; automatic requests require an incident ID. Repeated manual keys or automatic incidents return the original job rather than rebuilding its snapshot from mutable configuration.

`JobStatusChanged` adds optional `Phase`, `TargetEndpoint` and `Cutover`. Phases are Pending, Provisioning, TargetHealth, TrafficCutover, EntrypointVerification and Completed. Cutover outcomes are NotStarted, Applied, Verified and Uncertain. Worker must sequence phase/status events, sanitize failures and endpoints, and emit Succeeded only after target health and entrypoint verification; execution remains pending.

These fields are additive: existing message URNs and v1 fixtures remain valid, and Manual is the default trigger. New migration execution requires a non-null complete snapshot; a legacy request must not be treated as sufficient migration input. The v2 fixture demonstrates automatic submission with immutable inputs.

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

Tests verify v1/v2 JSON compatibility, message identities, delivery and correlation with MassTransit 8.5.8. PostgreSQL tests verify durable submission/outbox constraints. Local Compose provisions the durable topology. Worker execution and production service integration remain pending.
