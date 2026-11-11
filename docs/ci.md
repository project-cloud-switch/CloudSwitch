# CI checks

[CI](../.github/workflows/ci.yml) runs for every pull request, pushes to `main`, and manual dispatch. Both jobs restore and build the solution in Release. Actions are pinned to commit SHAs. Each job installs the exact SDK version from `global.json` in a fresh directory and verifies `dotnet --version`; the developer `rollForward` setting cannot select a newer SDK on the runner.

| Check name | Coverage |
| --- | --- |
| `Unit and contracts` | Tests with `Category!=ExternalIntegration`: contracts, serialization, in-memory messaging, Worker logic, JWT validation and API host behavior; no external services. |
| `PostgreSQL and RabbitMQ` | Tests with `Category=ExternalIntegration`: migrations, service role permissions, ownership, submission races, transactional outboxes, Worker fencing/recovery and real RabbitMQ delivery. Also checks for pending EF model changes. |

The integration job supplies all three `CLOUDSWITCH_TEST_*` variables to healthy PostgreSQL 17 and RabbitMQ 4 management service containers, then waits for the RabbitMQ management API before testing. These use disposable development credentials and need no Keycloak or cloud credentials. The tests sign their own JWTs. The database user must be able to create/drop databases and create/grant roles; the broker user needs management access to create/delete virtual hosts and grant permissions.

The TRX guard rejects missing/empty reports, skipped or failed results, incomplete execution counters, and missing persistence, Worker persistence or Worker messaging suites. Adding external integration test classes requires the `Category=ExternalIntegration` trait; add any new required suite to `scripts/verify_integration_results.py` as well. Its negative-path tests run in CI. Existing contracts tests tagged `Category=Integration` use in-memory messaging and run in the quick job.

Both jobs upload TRX artifacts, including on failure. The integration artifact also includes hang diagnostics and service logs when available. Artifacts are retained for seven days. Tests drop their random databases and RabbitMQ virtual hosts in teardown/finally blocks. GitHub removes the disposable service containers after the job, including on failure or cancellation, so a killed test cannot leave resources in a shared server.

## Run the same checks locally

Use the stable SDK version in `global.json` (`dotnet --version` must match `sdk.version`), Python 3, and Docker for integration. Run from the repository root:

```sh
dotnet restore CloudSwitch.sln
dotnet tool restore
dotnet build CloudSwitch.sln --configuration Release --no-restore
dotnet ef migrations has-pending-model-changes \
  --project src/CloudSwitch.Persistence --startup-project src/CloudSwitch.Persistence \
  --configuration Release --no-build
python3 -m unittest discover -s scripts/tests
dotnet test CloudSwitch.sln --configuration Release --no-build \
  --filter 'Category!=ExternalIntegration' --logger trx --results-directory TestResults/unit
```

Start only the two integration dependencies. Local Compose exposes PostgreSQL on **55432**, while the CI service container uses **5432**:

```sh
docker compose up -d --wait postgres rabbitmq
export CLOUDSWITCH_TEST_POSTGRES='Host=localhost;Port=55432;Database=cloudswitch;Username=cloudswitch;Password=cloudswitch-dev'
export CLOUDSWITCH_TEST_RABBITMQ='amqp://cloudswitch:cloudswitch-dev@localhost:5672'
export CLOUDSWITCH_TEST_RABBITMQ_MANAGEMENT='http://localhost:15672'
dotnet test tests/CloudSwitch.Api.Tests --configuration Release --no-build \
  --filter 'Category=ExternalIntegration' \
  --logger 'trx;LogFileName=integration.trx' --results-directory TestResults/integration \
  --blame-hang-timeout 3m
python3 scripts/verify_integration_results.py TestResults/integration/integration.trx
unset CLOUDSWITCH_TEST_POSTGRES CLOUDSWITCH_TEST_RABBITMQ CLOUDSWITCH_TEST_RABBITMQ_MANAGEMENT
docker compose stop postgres rabbitmq
```

Run the guard even when `dotnet test` exits successfully: VSTest otherwise treats skipped tests or an unmatched filter as success. Use development infrastructure; the tests create isolated databases/vhosts, apply the existing migration and role grants, and remove those resources. Compose volumes and existing development databases are preserved. Without the category filter, `dotnet test CloudSwitch.sln` still runs all tests and skips integrations when their environment variables are absent.

## Required checks and run evidence

Proposed branch protection checks are `Unit and contracts` and `PostgreSQL and RabbitMQ`. Confirm these names with the repository maintainer after the first hosted green run. This change does not modify branch protection or add deployment automation.

When the workflow is pushed, attach the successful run URL and its tested commit to [issue #23](https://github.com/project-cloud-switch/CloudSwitch/issues/23). Local passing results do not establish that the GitHub-hosted workflow is green.
