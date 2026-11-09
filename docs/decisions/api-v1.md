# API v1 implementation decisions

Status. Implemented control-plane baseline with PostgreSQL/RabbitMQ Worker integration. The agreed deployment-request and job-progress discussions define the shared message contracts. Local execution uses deterministic simulation adapters. Cloud execution, Doctor monitoring and Notifications remain pending.

| Decision | Baseline |
| --- | --- |
| Identity | Keycloak/OIDC bearer access tokens; unique `(issuer, subject)` maps to a server-generated user UUID. |
| Local authentication | Imported Keycloak realm; browser PKCE client, developer user and separate local service-account client for CLI verification. |
| Schema ownership | One `CloudSwitch.Persistence` project and migration history; explicit one-shot migration process. |
| Storage | PostgreSQL/EF Core; structured definitions/snapshots in JSONB, ownership/constraints/progress in indexed relational columns. |
| Configuration | Registered source, pinned image/resources/settings/secret references, health/recovery policy and traffic settings. Targets resolve operator-approved module/revision definitions. |
| Revisions | Increasing deployment/target revisions; ETag/If-Match on replacement and soft deletion. Freeze configuration while a job is active. |
| Secrets | `vault://path#key` reference fields, including optional registry credentials; execution resolves them. No credentials, arbitrary module sources or user-selected HCL in job requests. |
| Job submission | Shared API/Doctor service. Immutable source/target/application/health/traffic snapshot and outgoing request commit through the EF/MassTransit bus outbox. |
| Concurrency | Per-deployment PostgreSQL advisory transaction lock plus unique indexes: one active migration, one manual submission key, one automatic incident job. |
| Idempotency | Manual key scoped to deployment; same target returns original job, changed target returns 409. Repeated automatic incidents return the original job, including after failure. |
| Automatic recovery | Transaction rechecks monitoring, Automatic policy/approved target, current unhealthy incident/sequence and configuration/runtime revisions. Unknown never creates a job. |
| Runtime/health | Worker owns writes. Before initialization use registration as source; report Unknown without a current observation. |
| Progress | Pending → Running → Succeeded/Failed; phases Pending, Provisioning, TargetValidation, TrafficCutover, EntrypointVerification, Completed. Cutover: NotStarted, Applied, Verified, Uncertain. |
| Worker durability | PostgreSQL leases with owner/fencing/expiry checks, heartbeat renewal, bounded recovery attempts and persisted sequence. Progress, history and outgoing status messages commit through the EF/MassTransit bus outbox. |
| Execution boundary | Worker must verify health/entrypoint before success, bound attempts, renew/fence leases, reconcile checkpoints and preserve source infrastructure on failure. No API execution-state writes. |
| Doctor discovery | Initial Worker reads active PostgreSQL configuration on startup and polls revisions. Events and replica coordination are deferred. |
| Broker | Durable exchanges/queues/bindings provisioned before publishing. API does not consume Worker/Notifications queues. |
| Compatibility | Existing message URNs/v1 fixtures retained. Migration snapshot, trigger/incident and progress details are additive. The API maps its frozen PostgreSQL execution snapshot to the agreed flat `DeploymentSpecification`, including `DeploymentTrigger`, target/module, resources, Vault references, health policy and traffic entrypoint. |

See [working API and examples](../api.md), [domain model](../domain-model.md), [contracts](../contracts.md) and [role permissions](../../src/CloudSwitch.Persistence/DatabasePermissions.sql).
