# Dev Foundry

An AI development factory built around Forgejo, a FastAPI controller, and Temporal.

Forgejo sends signed webhooks to the controller API. Temporal stores each event
and dispatches it to an independently deployed controller worker. The current
workflow records event receipts; agent execution will be added as the factory grows.

## Documentation

| Guide | Covers |
| --- | --- |
| [Infrastructure](infra/README.md) | WSL setup, Compose services, local addresses, credentials, and data management |
| [Controller API](services/factory-controller-api/README.md) | API endpoints, signed Forgejo webhooks, and API unit tests |
| [Controller worker](services/factory-controller-worker/README.md) | Temporal workflows, event contract, and worker unit tests |
| [Integration tests](tests/integration/README.md) | Deployed API-to-worker pipeline over HTTP and Temporal |

Start with the [infrastructure setup](infra/README.md#start), then
[connect Forgejo to the API](services/factory-controller-api/README.md#connect-forgejo).

## Repository

- [`infra/`](infra/README.md): deployment configuration and infrastructure initialization.
- [`services/factory-controller-api/`](services/factory-controller-api/README.md): webhook API and its tests.
- [`services/factory-controller-worker/`](services/factory-controller-worker/README.md): Temporal worker, workflows, and its tests.
- [`tests/integration/`](tests/integration/README.md): tests spanning the independently deployed services.
