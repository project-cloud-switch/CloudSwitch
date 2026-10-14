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

The planned services use ASP.NET Core, PostgreSQL and RabbitMQ with MassTransit.

See the [architecture diagram and event flow](docs/architecture.md).

## Project status

Early development: the initial Contracts library and its tests are available. The services and end-to-end migration workflow are not implemented yet.

## Build and test

Requires a stable .NET 10 SDK.

```sh
dotnet restore CloudSwitch.sln
dotnet build CloudSwitch.sln --no-restore
dotnet test CloudSwitch.sln --no-build
```

The current tests use in-memory messaging and do not require Docker or cloud credentials.

## Development documentation

- [Service contracts](docs/contracts.md)
- [Migration model and development scope](docs/domain-model.md)

## License

[MIT](LICENSE)
