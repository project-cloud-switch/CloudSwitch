# Discussion: Shared persistence implementation

**Status: open implementation details; ownership is settled.** Suggested reviewers: Polina, Vlad and Egor. Related task: [#2](https://github.com/project-cloud-switch/CloudSwitch/issues/2).

## Agreed

API and Worker share PostgreSQL. API owns configuration and creates manual jobs; Doctor owns health/incidents and creates automatic jobs; Deployer owns execution state and active runtime. API reads progress directly. Notifications owns only its delivery state.

## Decide

- Minimum tables/columns needed for configuration, runtime, jobs, incidents and outboxes; match the agreed message snapshots.
- How API and Doctor apply identical job-creation constraints without duplicating inconsistent rules. Which project owns the shared schema/migrations?
- Enforce one active migration per deployment, unique automatic incident jobs and manual request idempotency.
- Transaction boundaries for incident creation, job submission, progress and active-target updates.
- Database permissions and how the single coordinated migration process runs locally and in CI.

Doctor configuration distribution and replica probe ownership remain a [separate discussion](doctor-configuration.md); do not assume per-replica queues or monitoring leases.

## Meeting output

A minimal schema outline, migration owner and transaction/write boundaries. No database implementation is required during the meeting.
