# Discussion: Contract behavior

**Status: open.** Suggested reviewers: Vlad and Stepan. Related tasks: [#2](https://github.com/project-cloud-switch/CloudSwitch/issues/2), [#5](https://github.com/project-cloud-switch/CloudSwitch/issues/5), [#14](https://github.com/project-cloud-switch/CloudSwitch/issues/14).

## Decide

- Validation rules for IDs, enums, image/configuration inputs, health thresholds and safe error categories. Which service rejects invalid input?
- How incident identity and correlation are created, preserved through migration, and closed after confirmed recovery or successful cutover.
- What happens when a manual migration races automatic recovery, or a policy changes during an incident?
- When a failed job may resume versus requiring a new user request. Persistent failure must not create endless automatic jobs.
- Which status/phase changes increment event sequence, and how consumers ignore duplicates or stale events.

## Keep fixed

NotifyOnly is the default. Automatic requires an approved target. JobId identifies execution; deployment/incident identifies an automatic request. Delivery may repeat, so services persist state and outgoing messages atomically and deduplicate.

## Meeting output

A short validation/transition table and agreed outcomes for duplicate delivery, stale observations, concurrent requests and retry exhaustion. These become test scenarios.
