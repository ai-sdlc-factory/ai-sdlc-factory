# Dev Foundry

An AI development factory built around Forgejo, an ASP.NET Core controller, and Temporal.
All services are C# on .NET 10; open [dev-foundry.slnx](dev-foundry.slnx) in your IDE.

Forgejo sends signed webhooks to the controller API. Temporal stores each event
and dispatches it to an independently deployed controller worker. The current
Forgejo workflow records event receipts. An optional OpenCode worker runs a manual
demo task through Temporal using ChatGPT browser authentication.

## Documentation

| Guide | Covers |
| --- | --- |
| [Infrastructure](infra/README.md) | WSL setup, Compose services, local addresses, credentials, and data management |
| [Controller API](services/api/README.md) | API endpoints, signed Forgejo webhooks, and API unit tests |
| [Controller worker](services/controller/README.md) | Temporal workflows, event contract, and worker unit tests |
| [OpenCode demo](services/opencode-worker/README.md) | Browser authentication, demo tasks, execution worker, and artifacts |
| [Integration tests](tests/integration/README.md) | Deployed API-to-worker pipeline over HTTP and Temporal |

Start with the [infrastructure setup](infra/README.md#start), then
[connect Forgejo to the API](services/api/README.md#connect-forgejo).

## Repository

- [`infra/`](infra/README.md): deployment configuration and infrastructure initialization.
- [`services/api/`](services/api/README.md): webhook API and its tests.
- [`services/controller/`](services/controller/README.md): Temporal worker, workflows, and its tests.
- [`tests/integration/`](tests/integration/README.md): tests spanning the independently deployed services.
- [`services/opencode-worker/`](services/opencode-worker/README.md): OpenCode execution worker and demo submitter.
