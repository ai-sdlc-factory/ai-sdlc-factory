# Factory Controller Worker

[Project overview](../../README.md) | [Infrastructure](../../infra/README.md) | [API](../api/README.md)

A standalone Temporal worker for trusted factory orchestration. It polls the
configured task queue and executes `ForgejoEventWorkflow`, which records a receipt
containing the event type, repository, action, and Git ref.

This service is a .NET 10 worker with its own project, dependencies, and Docker
image. It exposes no HTTP port and does not reference the API project. Compose passes it
Temporal settings only; it does not receive the Forgejo webhook secret.

## Run

Complete the [infrastructure setup](../../infra/README.md#start), then run from
`infra/`:

```bash
docker compose up -d --build controller
docker compose logs -f controller
```

Shared settings in `infra/.env` are `TEMPORAL_ADDRESS`, `TEMPORAL_NAMESPACE`
(default `default`), and `TEMPORAL_TASK_QUEUE` (default `factory-events`).
The API and controller must use the same namespace and task queue.

## OpenCode demo

The worker also runs `OpenCodeDemoWorkflow`, which delegates execution to a
separate activity worker. See the [OpenCode guide](../opencode-worker/README.md)
for browser login and task submission. Forgejo receipt workflows remain unchanged.

## Feature tickets

`ForgejoEventWorkflow` also routes Forgejo ticket events (decisions live in `EventRouter`):

- An open ticket labeled `feature` (on `issues` opened, reopened or label updated) starts
  `FeatureWorkflow` with the ID `feature-<owner>-<repo>-<number>`. Repeated events for the same
  ticket are ignored.
- A human comment on a feature ticket is signalled to its waiting workflow (`human_reply`);
  closing the ticket sends `ticket_closed`. Comments written by the factory carry a hidden marker and
  are ignored. Pull request events are ignored.
- `FeatureWorkflow` is a skeleton for now: it comments on the ticket, waits up to 7 days for a reply
  and finishes. The stages in [the design](../../docs/feature-factory.md) will be added to it.

The controller talks to Forgejo only through `ForgejoClient`, using the bot token from
`FORGEJO_BOT_TOKEN` (see [the bot account](../../infra/README.md#factory-bot-account)). Without a
token, feature workflows fail at their first comment and every other event still works.

## Workflow contract

The workflow input and the OpenCode task are defined once in
[`libs/DevFoundry.Contracts`](../../libs/DevFoundry.Contracts/Contracts.cs), shared with the API
and the OpenCode worker. The contract tests below pin their JSON names.

The API starts workflow type `ForgejoEventWorkflow` with this JSON envelope:

```json
{
  "delivery_id": "12345678-1234-1234-1234-123456789abc",
  "event": "issues",
  "payload": {
    "repository": {"full_name": "owner/repository"},
    "action": "opened"
  }
}
```

The workflow name and JSON format are unchanged from the earlier combined
controller and the Python services, so existing workflow histories and queued
events remain compatible. The receipt's `action` and `ref` are strings, or `null`
when the payload field is missing or not a string. The full payload is retained in
Temporal history. The current workflow records events; agent execution will be
added separately.

The worker writes `/tmp/worker-ready` while it runs; the Compose healthcheck tests
for that file.

## Tests

From the repository root:

```bash
docker build --target test -f services/controller/Dockerfile -t dev-foundry/controller-test .
docker run --rm dev-foundry/controller-test
```

These xUnit tests verify the JSON contract with the API. See the
[integration tests](../../tests/integration/README.md) for the full HTTP-to-workflow path.
