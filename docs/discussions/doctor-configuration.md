# Discussion: Doctor configuration and replicas

**Status: open.** Replica coordination is not an architecture decision yet.

## Question

How should Doctor receive configuration changes and resume monitoring after a restart, without making the first implementation unnecessarily complex?

API owns configuration in PostgreSQL. An API configuration event is the preferred direction. We also want updates to reach every Doctor that needs them.

## Configuration contract

- Should the event carry a full snapshot or a deployment/configuration reference that Doctor reads from PostgreSQL?
- What revision and enable/disable information is needed? How are deletion and restart handled?
- How does Doctor discover the new active endpoint after cutover?

The configuration contract can be agreed before committing to a future multi-replica design. Suggested reviewers: Polina and Vlad; related tasks: [#2](https://github.com/project-cloud-switch/CloudSwitch/issues/2) and [#14](https://github.com/project-cloud-switch/CloudSwitch/issues/14).

## Options to compare

- Start with one Worker replica and event-driven Doctor configuration. Revisit coordination when scaling.
- Broadcast updates to all Doctor replicas, with a subscription per replica and a database snapshot at startup. Decide separately whether all replicas probe or share responsibility.
- Use a shared Doctor scheduler that distributes probe work to Worker replicas, instead of maintaining a schedule in each replica.

The last proposal added temporary queues, snapshot/event merging, deletion markers and monitoring leases. That is a candidate to reconsider, not a requirement.

## Keep these guarantees

- Configuration changes, disable/delete operations and restarts must be handled correctly.
- Stale observations must not trigger recovery with outdated settings.
- Repeated observations/messages must not create duplicate incidents or migrations.

Choose the smallest approach that meets our current deployment needs before adding configuration contracts or replica infrastructure.

## Meeting output

Choose the initial update payload and delivery/restart approach. Record what remains deferred for replica scaling; do not treat every candidate mechanism as a requirement.
