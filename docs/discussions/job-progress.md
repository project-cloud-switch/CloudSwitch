# Discussion: JobStatusChanged

**Status: phase/endpoint/cutover schema selected; Worker publication remains pending.** See [API v1 decisions](../decisions/api-v1.md) and [contracts](../contracts.md). Suggested reviewers: Vlad and Stepan. Related tasks: [#2](https://github.com/project-cloud-switch/CloudSwitch/issues/2), [#9](https://github.com/project-cloud-switch/CloudSwitch/issues/9). The original questions follow for integration review.

## Original gap

The original event carried status, attempts, sequence and safe errors. The API baseline now adds optional phase, target endpoint and cutover outcome fields; Worker still needs to emit them during execution.

## Decide

- Define phase names for provisioning, target validation, traffic switching and entrypoint verification.
- Do phase changes emit events while status remains Running? How does their sequence advance?
- When is the target endpoint available, and which endpoint may be shown to the user?
- What proves success? Target health and entrypoint verification are required; agree the verification deadline and evidence.
- What information distinguishes provisioning failure, health failure and uncertain/partial cutover? Are existing terminal statuses sufficient?

## Meeting output

One successful event sequence and failure examples for health rejection and interrupted cutover. Agree the fields Notifications exposes through SignalR and API reads from job storage.
