# Migration model

Agreed design for [#2](https://github.com/project-cloud-switch/CloudSwitch/issues/2). Database entities and migrations are not implemented yet.

## Development scope

Sprint 2 monitors a stateless container on Yandex Cloud and supports manual or policy-driven migration to one fallback cloud: provision → validate health → switch traffic → verify entrypoint. Use saved application configuration when the source is unavailable.

Terraform only; OpenTofu, database/volume migration, automatic failback and source deletion are deferred. Local simulation supports CI; the demo uses real clouds. Run CloudSwitch, PostgreSQL and RabbitMQ outside the source failure domain. Image and traffic-control access must also survive the outage.

## Shared PostgreSQL model

| Record | Purpose | Write owner |
| --- | --- | --- |
| User | Auth identity and resource ownership. | API |
| Deployment | Saved application definition, monitoring/recovery policy, entrypoint and configuration revision. | API |
| TargetConfiguration | Approved provider/module inputs and secret references. | API |
| DeploymentRuntime | Active provider/endpoint and revision; initially the registered source, then the verified target. | Worker |
| Job | Immutable request snapshot, trigger, optional incident ID, status/phase, attempts, lease, checkpoints and results. | API/Doctor create; Deployer updates execution |
| JobEvent | Ordered job progress with event/correlation IDs and safe outcome. | Deployer |
| HealthObservation / Incident | Probe counters, status, sequence and outage episode, separate from jobs. | Doctor |

API reads job/health state directly. Worker initializes runtime state from registration. Doctor uses API-owned settings and the active runtime endpoint; discard observations from outdated settings or endpoints. Notifications owns only delivery/deduplication state. Store secret references, not credentials.

Configuration delivery, restart handling and probe ownership across replicas are [under discussion](discussions/doctor-configuration.md); no extra monitoring-lease table is agreed.

## Failure response

Doctor confirms failure/recovery using consecutive probe thresholds and publishes `DeploymentHealthChanged` through an outbox. `Unknown` does not trigger recovery.

Notifications alerts the owner. In `NotifyOnly`, no automatic job is created. In `Automatic`, Doctor checks current policy, health and active provider inside the job-creation transaction, saves one job per incident and publishes `DeploymentRequested`. API uses the same job-creation rules for manual requests.

After verified cutover, Deployer records the new active target and Doctor monitors it. Repeated probes must not create endless migrations after a failed job.

## Job lifecycle and constraints

`Pending → Running → Succeeded or Failed`

Deployer handles **provisioning → target health → cutover → entrypoint verification**. Succeeded requires both health and traffic verification. A failed health check prevents cutover. Preserve checkpoints and observed target/routing state on failure; leave the source untouched.

- One active migration per deployment, enforced for both API and Doctor.
- Unique `(DeploymentId, IncidentId)` for automatic jobs; repeated manual submission keys return the same job.
- Only the current lease holder can update execution. Bound attempts and fence expired workers.
- Recover expired jobs from checkpoints; reconcile actual infrastructure/routing before repeating external operations.
- Reuse target Terraform state across attempts; keep source and target state separate.
- Persist state and outgoing requests/events atomically through outboxes. Use unique event IDs and `(JobId, Sequence)`; index jobs by status/lease expiry.

Use separate API/Worker database roles and one coordinated schema-migration process. See the [architecture diagram](architecture.md), [#5: execution](https://github.com/project-cloud-switch/CloudSwitch/issues/5), [#7: recovery](https://github.com/project-cloud-switch/CloudSwitch/issues/7), [#12: target health](https://github.com/project-cloud-switch/CloudSwitch/issues/12) and [#14: Doctor](https://github.com/project-cloud-switch/CloudSwitch/issues/14).
