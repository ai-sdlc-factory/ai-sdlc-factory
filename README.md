# Dev Foundry

An AI development factory built around Forgejo, a FastAPI controller, and Temporal.

Forgejo sends signed webhooks to the controller API. Temporal stores each event
and dispatches it to a separate controller worker. The current workflow records
event receipts; agent execution will be added as the factory grows.

## Documentation

| Guide | Covers |
| --- | --- |
| [Infrastructure](infra/README.md) | WSL setup, Compose services, local addresses, credentials, and data management |
| [Factory controller](services/factory-controller/README.md) | API endpoints, Forgejo webhooks, Temporal worker, and tests |

Start with the [infrastructure setup](infra/README.md#start), then
[connect Forgejo to the controller](services/factory-controller/README.md#connect-forgejo).

## Repository

- [`infra/`](infra/README.md): deployment configuration and infrastructure initialization.
- [`services/factory-controller/`](services/factory-controller/README.md): controller API, worker, workflows, and tests.
