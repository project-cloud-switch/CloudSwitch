# Discussion: Establish the contract baseline

**Status: open.** Reviewers: all service owners. Related task: [#2](https://github.com/project-cloud-switch/CloudSwitch/issues/2).

## Ready when

- Manual/automatic requests, health transitions and migration progress have reviewed schemas and examples.
- Required fields, defaults, validation and compatibility rules are written down.
- JSON fixtures cover successful migration, confirmed failure/recovery and safe failure outcomes.
- Serialization and MassTransit transport tests verify agreed identities, routing and correlation. Service tasks cover database/worker behavior separately.
- Build/tests run in CI, and API, Worker and Notifications owners approve the baseline before it is merged.

## Align the task source of truth

Update [#2](https://github.com/project-cloud-switch/CloudSwitch/issues/2) and affected service issues after decisions. Some wording still treats storage ownership as open and API as a health-event consumer. Reflect shared PostgreSQL/direct reads and the final Doctor configuration approach consistently across issues and docs.

## Meeting output

Assign contract implementation, test/CI and issue-alignment owners. Agree a merge target and compatibility process; a project reference is sufficient initially, without adding package publishing infrastructure.
