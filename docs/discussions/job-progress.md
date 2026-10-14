# Discussion: JobStatusChanged

**Status: open.** Suggested reviewers: Vlad and Stepan. Related tasks: [#2](https://github.com/project-cloud-switch/CloudSwitch/issues/2), [#9](https://github.com/project-cloud-switch/CloudSwitch/issues/9).

## Current gap

The event carries status, attempts, sequence and safe errors. It lacks migration phase, target endpoint and the outcome of an interrupted cutover.

## Decide

- Define phase names for provisioning, target validation, traffic switching and entrypoint verification.
- Do phase changes emit events while status remains Running? How does their sequence advance?
- When is the target endpoint available, and which endpoint may be shown to the user?
- What proves success? Target health and entrypoint verification are required; agree the verification deadline and evidence.
- What information distinguishes provisioning failure, health failure and uncertain/partial cutover? Are existing terminal statuses sufficient?

## Meeting output

One successful event sequence and failure examples for health rejection and interrupted cutover. Agree the fields Notifications exposes through SignalR and API reads from job storage.
