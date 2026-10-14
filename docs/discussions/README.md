# Contract readiness discussions

Goal: agree on a small contract baseline that API, Worker and Notifications can implement independently. These notes are open questions, not finalized schemas.

Read [architecture](../architecture.md), [current contracts](../contracts.md) and [domain model](../domain-model.md) before the meeting.

| Topic | Decision needed | Suggested lead |
| --- | --- | --- |
| [Deployment request](deployment-request.md) | Shared manual/automatic request fields and snapshot contents. | Polina + Vlad |
| [Job progress](job-progress.md) | Phases, completion criteria and failure payload. | Vlad + Stepan |
| [Doctor configuration](doctor-configuration.md) | Update payload and initial delivery/restart approach. | Polina + Vlad |
| [Contract behavior](contract-behavior.md) | Validation, incidents, duplicates and retry/resume rules. | Vlad + Stepan |
| [Shared persistence](shared-persistence.md) | Tables, write boundaries and migration process. | Polina + Vlad + Egor |
| [Baseline release](contract-release.md) | Review, tests, issue alignment and merge readiness. | Everyone |

## Suggested meeting

Participants: Polina, Vlad, Stepan and Egor. Allow 45 minutes:

1. Deployment request — 10 minutes.
2. Job progress — 10 minutes.
3. Doctor configuration — 10 minutes. Keep future replica scaling separate if it blocks a simple starting approach.
4. Behavior and persistence — 10 minutes.
5. Record decisions, owners and follow-up tasks — 5 minutes.

For each topic, record **decision, owner and remaining action** in its note. Then update contracts, docs and affected GitHub issues together. The meeting should produce an implementable baseline, not resolve every future scaling concern.
