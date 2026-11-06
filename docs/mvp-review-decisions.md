# MVP scope and review decisions (6 November 2026)

This note reconciles the baseline docs with the open documentation PRs [#18](https://github.com/project-cloud-switch/CloudSwitch/pull/18), [#19](https://github.com/project-cloud-switch/CloudSwitch/pull/19), and [#20](https://github.com/project-cloud-switch/CloudSwitch/pull/20). **A review comment records a proposed/accepted direction, not proof that code has shipped.** Items marked *proposed* still need the relevant service owners' agreement before becoming contract fields.

## MVP boundary

- The only supported **source** deployment type is **Yandex Cloud Serverless Containers**. Do not imply that arbitrary Yandex VMs, Kubernetes deployments, or generic container hosts are supported as inputs.
- The fallback is a pre-approved **stateless container-on-VM** deployment driven by an allowlisted, pinned Terraform module. The exact target provider/module identifiers remain a contract decision.
- Store the source **folder ID, Serverless Container ID, and direct HTTPS invocation endpoint**. A revision ID may additionally identify the observed source version. Record the pinned container image and application configuration separately, before the source fails.
- Serverless Containers do not imply stateful volume migration. Reject configurations that require persistent/local state, databases, or volumes to be copied; recording an empty volumes list alone is not evidence that an app is stateless.
- No source destruction, database/volume migration, automatic failback, or general-purpose Terraform translation.

## Agreed flow and ownership

| Concern | Owner / rule |
| --- | --- |
| Manual migration | API validates ownership, creates a job and durable outbox message |
| Automatic migration | Doctor (inside Worker) confirms an incident, rechecks the current policy, creates a job and durable outbox message |
| Execution | Deployer (inside Worker) consumes the same `DeploymentRequested` contract |
| Configuration, deployment and target settings | API writes |
| Health observations and incidents | Doctor writes |
| Job creation | API for manual, Doctor for automatic; shared DB invariants apply to both |
| Job execution, phase, result and active runtime | Deployer writes |
| Notification delivery state | Notifications writes; API reads job/health state directly |

API and Doctor must enforce **database-level** uniqueness: at most one active job per deployment, one automatic migration association per `(DeploymentId, IncidentId)`, and no conflicting simultaneous cutover for a shared traffic entrypoint. Application-side existence checks alone are race-prone. Job and outbox records must be committed together. The precise shared job-creation library, EF migration owner, database constraints, and locking transaction are **still to be specified**; separate service EF migrations must not independently mutate the same shared tables.

## Review decisions to preserve

- **Auth:** Keycloak is the selected local IdP for development. API verifies JWTs with `aud=cloudswitch-api`; Keycloak `sub` (UUID) maps to the internal `Guid` user ID. A local `User` row is created on first authorized API access. Authentication in tests uses a test handler; local Compose includes Keycloak. Production placement outside the Yandex failure domain remains **undecided**.
- **Authorization:** enforce resource owner checks, not only a user-owned Vault path convention. Worker uses service-specific credentials for DB, broker, secrets and providers and does not need a user JWT to call the API in the current architecture.
- **Monitoring:** Doctor owns `HealthObservation` and `Incident`. Changing monitoring settings affects future checks. Switching to `NotifyOnly` while an incident is open prevents *new* automatic jobs, but does not close the incident or automatically cancel an already-submitted job.
- **API reads:** proposed endpoints `GET /deployments/{id}/health` (current state) and `GET /deployments/{id}/incidents` (history) both enforce deployment ownership.
- **Identifiers:** internal DB/message IDs remain `Guid`; prefixed REST identifiers (`dep-`, `job-`, etc.) were proposed and are not a change to contract ID types.

## Decisions still blocking implementation

1. **Doctor configuration delivery:** direct PostgreSQL reads with revision checks are the simplest Sprint 2 proposal. The main architecture previously called event delivery preferred; **no update event or queue has been approved**. Document only one initial behavior after Vlad/Polina agree how polling, changes, deletion and restart work.
2. **Migration snapshot schema:** finalize fields and requiredness for source/target, pinned image digest, container configuration, secret references, Terraform module name and revision, target health check and DNS/HTTPS cutover. Snapshots must not depend on live Yandex control-plane reads at submission or execution.
3. **Event schemas:** agree `JobStatusChanged` phase names, safe error codes and optional target endpoint, and `DeploymentHealthChanged.reasonCode`; until then do not present guessed enum values as finalized.
4. **Health policy bounds:** confirm interval, timeout, failure/recovery thresholds, URL/redirect/SSRF rules and monotonically increasing configuration revision.
5. **Traffic locking and routing:** define canonical traffic-entrypoint identity, supported DNS provider, requested TTL, HTTPS certificate/hostname prerequisites and cutover verification evidence.
6. **Persistence mechanics:** name the single migration owner and common job creation/concurrency implementation, and decide when `DeploymentRuntime` is initialized at registration.
7. **Auth deployment:** decide how Keycloak, databases, API and Worker remain reachable when the source cloud is unavailable.

See the [architecture](architecture.md), [domain model](domain-model.md), [contracts](contracts.md) and [readiness discussions](discussions/README.md). Do not merge these proposed contract choices without updating C# contracts, JSON fixtures and tests.
