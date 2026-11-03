# Dev Foundry

An AI development factory built around Forgejo, an ASP.NET Core controller, and Temporal.
All services are C# on .NET 10; open [dev-foundry.slnx](dev-foundry.slnx) in your IDE.

Forgejo sends signed webhooks to the controller API. Temporal stores each event
and dispatches it to an independently deployed controller worker. The current
Forgejo workflow routes feature tickets to a long-running workflow that turns each ticket into a
reviewed pull request, with role agents running in disposable OpenCode containers (see the
[feature factory design](docs/feature-factory.md)).

## Documentation

| Guide | Covers |
| --- | --- |
| [Infrastructure](infra/README.md) | WSL setup, Compose services, local addresses, credentials, and data management |
| [Controller API](services/api/README.md) | API endpoints, signed Forgejo webhooks, and API unit tests |
| [Controller worker](services/controller/README.md) | Temporal workflows, event contract, and worker unit tests |
| [OpenCode login](docs/opencode-login.md) | Signing the agents in with a ChatGPT account, and sharing the login with sandboxes |
| [Integration tests](tests/integration/README.md) | Deployed API-to-controller pipeline over HTTP and Temporal |
| [Feature factory design](docs/feature-factory.md) | Planned ticket-to-PR workflow with role agents in disposable containers |

Start with the [infrastructure setup](infra/README.md#start), then
[connect Forgejo to the API](services/api/README.md#connect-forgejo).

## Repository

- [`infra/`](infra/README.md): deployment configuration and infrastructure initialization.
- [`services/api/`](services/api/README.md): webhook API and its tests.
- [`services/controller/`](services/controller/README.md): Temporal worker, workflows, and its tests.
- [`tests/integration/`](tests/integration/README.md): tests spanning the independently deployed services.
- [`services/sandbox-launcher/`](services/sandbox-launcher/README.md): the only service with Docker access; starts disposable agent containers.
- [`sandboxes/`](services/sandbox-launcher/README.md#images): Dockerfiles for the images agents run in.
- [`agents/`](agents/README.md): agent definitions (one Markdown file per role) and shared OpenCode settings.
- [`libs/DevFoundry.Contracts/`](libs/DevFoundry.Contracts/Contracts.cs): wire types shared by the services (Forgejo event, sandbox run request and result).
- [`bitbucket-pipelines.yml`](bitbucket-pipelines.yml): CI running the service tests, a vulnerable-package check, and the integration tests.

Build settings live in [`Directory.Build.props`](Directory.Build.props) and NuGet versions in
[`Directory.Packages.props`](Directory.Packages.props); change a package version there, not in a project file.
