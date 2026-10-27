# Factory Controller

FastAPI receives signed Forgejo webhooks and starts a Temporal workflow for each
delivery. A separate worker records a receipt with the event type, repository,
action, and Git ref. The full JSON payload is stored in Temporal workflow history.
Agent execution and repository changes will be added later.

## Run

Set `FORGEJO_WEBHOOK_SECRET` in `infra/.env` to at least 32 characters
(`openssl rand -hex 32`). Keep this secret out of Git and use the same value in
Forgejo's webhook settings.

From `infra/`:

```bash
docker compose up -d --build controller-api controller-worker forgejo
```

| Endpoint | Purpose |
| --- | --- |
| http://localhost:8000/docs | API documentation |
| http://localhost:8000/health/live | API process is responding |
| http://localhost:8000/health/ready | Temporal namespace is reachable |
| `POST /webhooks/forgejo` | Signed Forgejo event intake |

Configuration is shared through `infra/.env`: `TEMPORAL_ADDRESS`,
`TEMPORAL_NAMESPACE` (default `default`), `TEMPORAL_TASK_QUEUE` (default
`factory-events`), and `CONTROLLER_API_PORT` (default `8000`). Only the API
receives `FORGEJO_WEBHOOK_SECRET`; the worker does not need it.

## Connect Forgejo

In a repository, open **Settings > Webhooks > Add webhook > Forgejo**:

| Field | Value |
| --- | --- |
| Target URL | `http://controller-api:8000/webhooks/forgejo` |
| Method | `POST` |
| Content type | `application/json` |
| Secret | `FORGEJO_WEBHOOK_SECRET` from `infra/.env` |
| Events | Start with pushes, issues, and pull requests |

Enable the webhook and use **Test Delivery**. Forgejo runs inside Docker, so
the target uses `controller-api`, not `localhost`. Compose allows this hostname
in Forgejo's webhook host list; recreate Forgejo with the command above to apply it.

A valid delivery returns `202` with a `workflow_id`. Find that ID in
http://localhost:8080 under namespace `default`. The worker completes the receipt
workflow; acceptance does not mean that any engineering work has been performed.

The API verifies `X-Forgejo-Signature` as HMAC-SHA256 over the exact request bytes.
It requires `X-Forgejo-Event`, a UUID `X-Forgejo-Delivery`, and a JSON object.
Payloads larger than 1 MiB are rejected.

Repeated delivery IDs return `200` with status `duplicate`, including after a
workflow completes. Deduplication lasts while Temporal retains its execution
history (currently seven days after completion). If Temporal cannot confirm
acceptance, the API returns `503`; redeliver the event from Forgejo after recovery.

Inspect logs with `docker compose logs -f controller-api controller-worker`.

## Tests

From the repository root:

```bash
docker build --target test -t dev-foundry/controller-test services/factory-controller
docker run --rm dev-foundry/controller-test
```

To include an integration test against the running Compose Temporal service:

```bash
docker run --rm --network dev-foundry_default \
  -e TEMPORAL_TEST_ADDRESS=temporal:7233 dev-foundry/controller-test
```

The integration test uses a unique task queue and creates one test workflow
in the `default` namespace. It verifies completion, duplicate handling, and
signature rejection.
