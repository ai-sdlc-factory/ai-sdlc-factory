# Factory Controller API

[Documentation index](../../docs/README.md) | [Architecture](../../docs/architecture.md) | [Infrastructure](../../infra/README.md) | [Controller](../controller/README.md)

An ASP.NET Core (.NET 10) minimal API verifies signed webhooks (each kind of git host through its own webhook adapter, Forgejo's and GitHub's; one route per host) and starts
Temporal workflows. This service has its own project and Docker image. It sends a JSON envelope to the
`RepoEventWorkflow` workflow type and does not import or run worker code.

## Run

Complete the [infrastructure setup](../../infra/README.md#start). Set
`FACTORY_HOSTS` in `infra/.env` so that every host has a `webhook_secret` of at least 32 characters
(`openssl rand -hex 32`), matching the secret configured in that host's webhook.

From `infra/`:

```bash
docker compose up -d --build api
```

The [controller](../controller/README.md) must also be
running to process workflows. The API acknowledges events once Temporal stores
them, even if the worker is temporarily stopped.

| Endpoint | Purpose |
| --- | --- |
| http://localhost:8000/openapi/v1.json | OpenAPI document |
| http://localhost:8000/health/live | API process is responding |
| http://localhost:8000/health/ready | Temporal namespace is reachable |
| `POST /webhooks/<name>` | Signed event intake of the git host called `<name>` in `FACTORY_HOSTS` (`/webhooks/forgejo`, `/webhooks/github`, ...). A name that is not configured answers `404` |

Settings come from `infra/.env`: `TEMPORAL_ADDRESS`, `TEMPORAL_NAMESPACE`
(default `default`), `TEMPORAL_TASK_QUEUE` (default `factory-events`),
`CONTROLLER_API_PORT` (default `8000`), and `FACTORY_HOSTS` (the hosts and their webhook secrets).

## Connect a git host

In a repository on the host, add a webhook that points at `http://<api>/webhooks/<name>` (`<name>` is the host's `name` in `FACTORY_HOSTS`), with the host's `webhook_secret` and content type `application/json`. The events to send, the labels and the rest are in [Setting up a project](../../docs/project-setup.md#2-add-the-webhook); GitHub's setup is under [Several git hosts](../../docs/operations.md#several-git-hosts). On a Forgejo that runs in Docker (the one in [`forgejo/`](../../companions/forgejo/README.md)), the target is `http://api:8000/webhooks/forgejo`: it reaches the api over the Compose hostname `api`, which Forgejo's webhook host list allows. Use the host's **Test Delivery** (or **Redeliver**) to try it.

A valid delivery returns `202` with a `workflow_id`. Find it in
http://localhost:8080 under the configured namespace. The controller records an event
receipt and routes the event: ticket events start or signal the ticket's workflow ([routing](../../docs/workflows.md#ticket-routing)); other events are only recorded.

## Delivery behavior

The API verifies the host's signature header (Forgejo: `X-Forgejo-Signature`; GitHub: `X-Hub-Signature-256`) as HMAC-SHA256 over the exact request bytes, with the secret of the host the route names.
It requires the host's event header, a UUID delivery id header, and a JSON object.
Payloads larger than 1 MiB are rejected.

Repeated delivery IDs return `200` with status `duplicate`, including after a
workflow completes. Deduplication lasts while Temporal retains execution history
(currently seven days after completion). If Temporal cannot confirm acceptance,
the API returns `503`; redeliver the event from the git host after recovery. The API
connects to Temporal on first use, so it starts even while Temporal is down;
`/health/ready` reports `503` until Temporal answers.

Inspect logs with `docker compose logs -f api` from `infra/`.

## Tests

From the repository root:

```bash
docker build --target test -f services/api/Dockerfile -t dev-foundry/api-test .
docker run --rm dev-foundry/api-test
```

These xUnit tests run without Temporal or the worker. Use the
[integration tests](../../tests/integration/README.md) to verify the full pipeline.
