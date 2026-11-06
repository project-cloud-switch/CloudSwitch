# Discussion: JobStatusChanged

**Status: agreed.** Reviewers: Vlad and Stepan. Related tasks: [#2](https://github.com/project-cloud-switch/CloudSwitch/issues/2), [#5](https://github.com/project-cloud-switch/CloudSwitch/issues/5), [#9](https://github.com/project-cloud-switch/CloudSwitch/issues/9).

## Current gap

The previous event carried status, attempts, sequence and safe errors, but lacked the migration phase and target endpoint, leaving consumers unaware of granular progress across long-running provisioning and cutover steps.

## Agreed decisions

1. **Migration phases (`MigrationPhase` enum):**
   - `Pending`: Job created, awaiting lease acquisition.
   - `Provisioning`: Terraform apply in progress on fallback cloud.
   - `TargetValidation`: Target container deployed; HTTP health check probe in progress.
   - `TrafficCutover`: Target health confirmed; DNS/traffic entrypoint mutation in progress.
   - `EntrypointVerification`: Probing the public entrypoint to confirm end-to-end routing.
   - `Completed`: Verified cutover complete; target runtime state recorded.

2. **Phase event progression and sequence:**
   - Every phase transition emits a `JobStatusChanged` event through MassTransit Outbox.
   - The per-job `Sequence` strictly increments (+1) on each phase change, even while `Status` remains `Running`.
   - Consumers (Notifications / SignalR) deduplicate and drop out-of-order events using `(JobId, Sequence)`.

3. **Target endpoint visibility:**
   - Added `TargetEndpoint` (`string?`) to `JobStatusChanged`.
   - Populated upon entering `TargetValidation` (e.g. `http://185.12.34.56:8080`) to provide direct diagnostic visibility before public DNS is mutated.

4. **Failure semantics and cutover protection:**
   - Target health check failure prevents traffic cutover and sets `Status = Failed` with `Phase = TargetValidation` and safe `ErrorCode` / `ErrorMessage`.
   - Uncertain or interrupted cutovers record `Phase = TrafficCutover` with explicit failure codes so manual reconciliation or bounded retry can safely take place without corrupting DNS.

5. **Implementation status:**
   - `MigrationPhase` enum and enriched `JobStatusChanged` event implemented in `CloudSwitch.Contracts`.
   - Unit and serialization tests added in `CloudSwitch.Contracts.Tests`.

