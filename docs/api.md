# Working API

The API supports Keycloak authentication, owned deployment/target configuration, recovery policies, health reads and durable migration job submission. The [agreed request](discussions/deployment-request.md) and [progress](discussions/job-progress.md) discussions define the messages, and [API v1 decisions](decisions/api-v1.md) describe the shared schema. Worker and Notifications remain separate implementations.

## Start locally

Requires .NET 10 and Docker:

```sh
docker compose up -d --wait
dotnet run --project src/CloudSwitch.Api -- --migrate
dotnet run --project src/CloudSwitch.Api
```

| Service | Address | Development access |
| --- | --- | --- |
| API | `http://localhost:5080` | Bearer token |
| OpenAPI | `http://localhost:5080/openapi/v1.json` | Development only |
| Keycloak | `http://localhost:8080` | Admin: `admin` / `cloudswitch-dev` |
| Keycloak realm user | `cloudswitch` realm | `developer` / `cloudswitch-dev` |
| RabbitMQ management | `http://localhost:15672` | `cloudswitch` / `cloudswitch-dev` |
| PostgreSQL | `localhost:55432`, database `cloudswitch` | `cloudswitch` / `cloudswitch-dev` |

Ports bind to loopback. Checked-in credentials are development fixtures. Named volumes preserve data; `docker compose stop` stops services without deleting it. Keycloak imports the realm when absent, and does not overwrite an existing realm when the import file changes. RabbitMQ definitions create the development user and durable topology before publishing; its stable hostname preserves its node identity across recreations.

The root `compose.yaml` uses the development configuration in `docker/keycloak` and `docker/rabbitmq`. Deployment automation manages production service configuration and credentials. Database schema and permissions belong to `CloudSwitch.Persistence`.

## Authentication

The realm includes `cloudswitch-web`, a browser client using authorization code with PKCE and redirects under `http://localhost:3000/`. The local confidential client `cloudswitch-dev` supports client credentials for CLI development; its fixture secret is `cloudswitch-dev-secret`. Service accounts own their own resources; browser users have separate ownership.

Obtain a development access token:

```sh
curl -s http://localhost:8080/realms/cloudswitch/protocol/openid-connect/token \
  -d grant_type=client_credentials \
  -d client_id=cloudswitch-dev \
  -d client_secret=cloudswitch-dev-secret
```

Send `Authorization: Bearer <access_token>`. The API maps a unique `(iss, sub)` pair to its own user UUID on the first authenticated request. `/api/v1/me` returns that internal ID, issuer, subject and optional username. Issuer, audience, signature and expiry are validated; original claim names are preserved. Keycloak adds the `cloudswitch-api` audience through a mapper. New endpoints require authentication by default; the API has no password/token issuance endpoints or administrator bypass.

Production sets `Authentication__Authority` to its HTTPS realm and `Authentication__Audience` to its API audience. `Authentication__RequireHttpsMetadata` cannot be disabled outside Development. Provision production clients separately from the local import. See [ASP.NET JWT validation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0) and [Keycloak's container guide](https://www.keycloak.org/server/containers).

## Endpoints

All `/api/v1` routes require authentication. Other users' resources return 404. Lists accept `offset` and `limit` (default 50, maximum 100).

| Method | Route | Behavior |
| --- | --- | --- |
| GET | `/api/v1/me` | Get/create the application account. |
| GET | `/api/v1/modules` | List operator-approved modules. |
| POST / GET | `/api/v1/deployments` | Register/list owned deployments. |
| GET / PUT / DELETE | `/api/v1/deployments/{id}` | Read/replace/soft-delete configuration. |
| GET | `/api/v1/deployments/{id}/health` | Current health/runtime; Unknown until Doctor records a current observation. |
| POST / GET | `/api/v1/deployments/{id}/targets` | Create/list fallback configurations. |
| GET / PUT / DELETE | `/api/v1/deployments/{id}/targets/{targetId}` | Read/replace/soft-delete a target. |
| POST | `/api/v1/deployments/{id}/jobs` | Durably submit a manual migration. |
| GET | `/api/v1/jobs?deploymentId={id}` | List owned jobs, optionally filtered by deployment. |
| GET | `/api/v1/jobs/{id}` | Read persisted progress and submission snapshot. |
| GET | `/api/v1/jobs/{id}/events?afterSequence=0` | Read ordered events; also accepts `limit`. |
| GET | `/health/live` | Anonymous process liveness. |
| GET | `/health/ready` | Anonymous database/schema and broker checks; failure returns 503. |

Deployment/target GET, create and update responses include `ETag: "<revision>"`. PUT and DELETE require that value in `If-Match`: missing headers return 428; stale revisions return 412. Configuration cannot change while a job is active (409). Soft deletion preserves history and never deletes source infrastructure. A recovery policy reference must be removed before deleting its target.

Manual submission requires one `Idempotency-Key` header containing 1–128 characters. The first accepted submission returns 202 and `Location`. Repeating the same deployment/key/target returns 200 with the original job, including after later configuration edits. Reusing the key for another target returns 409; another active job also returns 409. Optional `X-Correlation-ID` must be a nonempty UUID; its persisted value is returned and propagated to messaging.

Errors use Problem Details with a trace ID; application errors include a stable `code`. Clients cannot write execution status or health observations.

Error responses use `application/problem+json`, including when the client's `Accept` header requests another format. Transient database outages return 503 with `code` set to `database_unavailable`. Unexpected failures return 500 with `code` set to `internal_error`, while diagnostics are recorded in server logs. OpenAPI describes required request headers, authentication and operation error responses.

Rate limits use one-minute fixed windows. Each authenticated `(iss, sub)` identity has a budget of 120 requests, and each anonymous connection IP has a budget of 30 requests. Job submission attempts also consume a separate budget of 5 requests per identity across all deployments. Token refreshes share the identity's existing budgets. Rejected requests return 429 with `Retry-After` in seconds and Problem Details containing `code: rate_limit_exceeded`. Health probes bypass these limits.

Configure positive limits through `RateLimiting__AuthenticatedRequestsPerMinute`, `RateLimiting__AnonymousRequestsPerMinute` and `RateLimiting__JobSubmissionsPerMinute`. Counters belong to each API process and reset on restart. Requests through one proxy share its anonymous IP budget. A shared gateway can enforce budgets across multiple API instances. The implementation uses [ASP.NET Core rate limiting middleware](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0).

## Application and target inputs

POST `/api/v1/deployments`:

```json
{
  "name": "demo",
  "definition": {
    "environment": "development",
    "sourceProvider": "yandex",
    "sourceEndpoint": "https://app.example.test",
    "application": {
      "image": "nginx@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "port": 80,
      "cpuMillicores": 500,
      "memoryMegabytes": 256,
      "settings": {"LOG_LEVEL": "info"},
      "secretReferences": {"API_KEY": "vault://demo#api-key"}
    },
    "healthCheck": {
      "path": "/health",
      "intervalSeconds": 10,
      "timeoutSeconds": 3,
      "failureThreshold": 3,
      "recoveryThreshold": 2
    },
    "traffic": {"driver": "simulation", "recordName": "app.example.test"}
  }
}
```

Images accept an explicit tag or a complete SHA-256 digest. Replace the fixture digest with a real image reference before cloud execution. Optional `application.imagePullSecretReference` carries a Vault registry credential reference. Monitoring defaults to enabled; recovery defaults to NotifyOnly. HTTP(S) source endpoints cannot contain credentials, queries or fragments and must match `Endpoints:AllowedHosts`. Local examples allow `localhost`, `127.0.0.1` and `app.example.test`. Doctor/Deployer must validate resolved destinations and redirects at execution time.

Health paths are local application paths; interval is 1–3600 seconds, timeout 1–60 seconds (no greater than interval), and thresholds 1–100. CPU is 100–64000 millicores; memory is 64–262144 MB. Traffic drivers are `simulation` and `cloudflare-dns`; the latter requires a zone reference and secret credential reference. These describe future execution, not API-side infrastructure changes.

POST `/api/v1/deployments/{id}/targets`:

```json
{
  "provider": "local",
  "moduleId": "local-stateless-v1",
  "inputs": {"region": "local"},
  "secretReferences": {"cloud": "vault://demo#cloud"}
}
```

The API resolves module source/revision from the operator's `Terraform:Modules` allowlist. Callers cannot provide arbitrary HCL/module sources or undeclared inputs. Production must approve real modules with immutable revisions; the local entry reserves a simulation module identity for Worker development. Credentials belong in a secret store, referenced as `vault://path#key`, never in settings or module inputs. Execution resolves references.

PUT the deployment with its ETag and set `definition.recovery` to `{"mode":"Automatic","targetConfigurationId":"<target UUID>"}` when wanted. The target must belong to this deployment and remain approved. Submit manually with `{"targetConfigurationId":"<target UUID>"}` and an `Idempotency-Key`.

## Persistence and deployment

`CloudSwitch.Persistence` owns the EF model, migrations and common API/Doctor job-creation service. Jobs and immutable requests commit with MassTransit's PostgreSQL outbox; only committed messages are delivered, with retries during broker outages. Direct submissions can be saved during a broker outage while readiness reports 503.

Database indexes enforce one active Pending/Running migration, unique manual keys and unique automatic incidents. Transaction-scoped advisory locks serialize configuration changes and submissions per deployment. Automatic requests recheck policy, monitoring, active incident/sequence and configuration/runtime revisions inside the transaction. Unknown does not trigger recovery.

Apply schema changes once using a migration-owner connection:

```sh
dotnet run --project src/CloudSwitch.Api -- --migrate
```

For production, supply `ConnectionStrings__Postgres` with the migration owner's credentials for this process. After migrations, database provisioning applies `src/CloudSwitch.Persistence/DatabasePermissions.sql` with credentials permitted to create roles and grant privileges. The file defines NOLOGIN API/Worker roles, which provisioning grants to separately created runtime logins. API cannot update execution state; Worker cannot update application configuration or immutable snapshots. Local Compose uses its development owner login for convenience. Normal startup never runs migrations.

To author migrations:

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/CloudSwitch.Persistence --startup-project src/CloudSwitch.Persistence
```

The design-time factory reads `ConnectionStrings__Postgres` or uses the local connection. Runtime settings include the authority/audience, PostgreSQL connection, `RabbitMq__Host`, `RabbitMq__Port`, `RabbitMq__VirtualHost`, `RabbitMq__Username`, `RabbitMq__Password`, approved endpoint hosts and module definitions. Use standard .NET configuration/secret providers. Keep the control plane, image access, secrets and traffic control outside the source failure domain.

## Verification and remaining services

See [CI checks and local reproduction](ci.md) for the exact Release build, unit/integration filters, EF model check and integration result guard used on pull requests and `main`.

```sh
dotnet test CloudSwitch.sln
```

Default JWT/host/contract tests need no external services. PostgreSQL tests are skipped unless `CLOUDSWITCH_TEST_POSTGRES` supplies a development admin connection. They create a random database, migrate it, test ownership/revisions/concurrent submissions/outbox rollback/automatic recovery/role permissions, then drop only that test database. Their broker is deliberately unavailable to verify durability. For example:

```sh
CLOUDSWITCH_TEST_POSTGRES='Host=localhost;Port=55432;Database=cloudswitch;Username=cloudswitch;Password=cloudswitch-dev' dotnet test CloudSwitch.sln
```

Worker now consumes the agreed request queue using RabbitMQ, claims and renews PostgreSQL leases, persists phase/sequence/checkpoints, and commits status history and outgoing events through the MassTransit EF bus outbox. It activates the target runtime only after verified completion. Expired owners cannot write progress. Interrupted cutovers fail with an uncertain outcome for manual reconciliation.

Run `dotnet run --project src/CloudSwitch.Worker` for explicit Development simulation against an isolated local database. Simulation accepts the approved `local-stateless-v1:local-simulation-v1` target. Provisioning, target health and traffic verification use deterministic adapters. Real Terraform, Vault resolution, HTTP/DNS adapters, Doctor monitoring and Notifications remain pending. Unobserved health stays Unknown.

PostgreSQL tests also cover Worker fencing, restart sequence continuity, progress/outbox rollback, and API progress reads. Set `CLOUDSWITCH_TEST_RABBITMQ` to `amqp://cloudswitch:cloudswitch-dev@localhost:5672` alongside the PostgreSQL connection to run the broker integration test. It creates and removes a random virtual host. The management API defaults to `http://localhost:15672`, overridable through `CLOUDSWITCH_TEST_RABBITMQ_MANAGEMENT`. The broker test starts the Worker before publishing and waits for verified job completion through the API.
