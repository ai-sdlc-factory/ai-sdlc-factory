# Factory Controller API

[Project overview](../../README.md) | [Infrastructure](../../infra/README.md) | [Worker](../controller/README.md)

An ASP.NET Core (.NET 10) minimal API verifies signed Forgejo webhooks and starts
Temporal workflows. This service has its own project and Docker image. It sends a JSON envelope to the
`ForgejoEventWorkflow` workflow type and does not import or run worker code.

## Run

Complete the [infrastructure setup](../../infra/README.md#start). Set
`FORGEJO_WEBHOOK_SECRET` in `infra/.env` to at least 32 characters
(`openssl rand -hex 32`), matching the secret configured in Forgejo.

From `infra/`:

```bash
docker compose up -d --build api forgejo
```

The [controller worker](../controller/README.md) must also be
running to process workflows. The API acknowledges events once Temporal stores
them, even if the worker is temporarily stopped.

| Endpoint | Purpose |
| --- | --- |
| http://localhost:8000/openapi/v1.json | OpenAPI document |
| http://localhost:8000/health/live | API process is responding |
| http://localhost:8000/health/ready | Temporal namespace is reachable |
| `POST /webhooks/forgejo` | Signed Forgejo event intake |

Settings come from `infra/.env`: `TEMPORAL_ADDRESS`, `TEMPORAL_NAMESPACE`
(default `default`), `TEMPORAL_TASK_QUEUE` (default `factory-events`),
`CONTROLLER_API_PORT` (default `8000`), and `FORGEJO_WEBHOOK_SECRET`.

## Connect Forgejo

In a repository, open **Settings > Webhooks > Add webhook > Forgejo**:

| Field | Value |
| --- | --- |
| Target URL | `http://api:8000/webhooks/forgejo` |
| Method | `POST` |
| Content type | `application/json` |
| Secret | `FORGEJO_WEBHOOK_SECRET` from `infra/.env` |
| Events | Start with pushes, issues, and pull requests |

Enable the webhook and use **Test Delivery**. Forgejo uses the Compose hostname
`api`; its webhook host list is configured in Compose.

A valid delivery returns `202` with a `workflow_id`. Find it in
http://localhost:8080 under the configured namespace. The worker records an event
receipt; agent execution and repository changes will be added later.

## Delivery behavior

The API verifies `X-Forgejo-Signature` as HMAC-SHA256 over the exact request bytes.
It requires `X-Forgejo-Event`, a UUID `X-Forgejo-Delivery`, and a JSON object.
Payloads larger than 1 MiB are rejected.

Repeated delivery IDs return `200` with status `duplicate`, including after a
workflow completes. Deduplication lasts while Temporal retains execution history
(currently seven days after completion). If Temporal cannot confirm acceptance,
the API returns `503`; redeliver the event from Forgejo after recovery. The API
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
