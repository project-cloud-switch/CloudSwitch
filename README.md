# CloudSwitch

CloudSwitch is an open-source platform in development for deploying and moving stateless container applications between cloud providers using Terraform.

It aims to monitor application availability and support configurable recovery: notify the owner or recreate the deployment on another provider, verify health, and switch traffic.

## Architecture

| Project | Responsibility |
| --- | --- |
| `CloudSwitch.Api` | Authentication, deployment configuration and job submission. |
| `CloudSwitch.Worker` | Doctor monitors health; Deployer executes provisioning and traffic cutover. |
| `CloudSwitch.Notifications` | Live job updates through SignalR. |
| `CloudSwitch.Contracts` | Shared integration messages. |
| `CloudSwitch.Persistence` | Shared PostgreSQL schema, migrations and durable API/Doctor job creation. |

The planned services use ASP.NET Core, PostgreSQL and RabbitMQ with MassTransit.

See the [architecture diagram and event flow](docs/architecture.md).

## Project status

The API supports Keycloak authentication, deployment/target configuration, recovery policies, health reads and durable migration job submission through a PostgreSQL outbox. Worker consumes RabbitMQ requests, persists fenced leases and progress in PostgreSQL, and publishes status through the transactional outbox. Its provisioning, health and traffic adapters currently simulate execution. Doctor monitoring, real cloud adapters and Notifications/SignalR remain pending.

## Run the API locally

Requires Docker for PostgreSQL, RabbitMQ and local Keycloak:

```sh
docker compose up -d --wait
dotnet run --project src/CloudSwitch.Api -- --migrate
dotnet run --project src/CloudSwitch.Api
```

The API listens at `http://localhost:5080`, Keycloak at `http://localhost:8080`. See [API setup and examples](docs/api.md).

## Run Worker simulation

After starting local infrastructure and applying migrations, run Worker in a second terminal. Its Development launch profile explicitly enables simulation for the approved local target.

```sh
dotnet run --project src/CloudSwitch.Worker
```

Worker requires Development and `Worker:Simulation=true`. Its deterministic adapters simulate provisioning, health checks and traffic changes. The PostgreSQL job/runtime state and RabbitMQ messaging are real. Use an isolated development database for simulation.

## Build and test

Requires a stable .NET 10 SDK.

```sh
dotnet restore CloudSwitch.sln
dotnet build CloudSwitch.sln --no-restore
dotnet test CloudSwitch.sln --no-build
```

Default tests use locally signed tokens and in-memory messaging. Optional PostgreSQL tests use `CLOUDSWITCH_TEST_POSTGRES` and an isolated database; see [verification](docs/api.md#verification-and-remaining-services). No cloud credentials are needed.

[CI checks and exact local commands](docs/ci.md) cover the separate unit/contracts and PostgreSQL/RabbitMQ jobs, including migration consistency and detection of skipped integration tests.

## Development documentation

- [Service contracts](docs/contracts.md)
- [Migration model and development scope](docs/domain-model.md)
- [API setup and authentication](docs/api.md)
- [API v1 implementation decisions](docs/decisions/api-v1.md)

## License

[MIT](LICENSE)
