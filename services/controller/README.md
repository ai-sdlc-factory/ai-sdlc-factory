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

### Analyst stage

After the workspace is ready, `FeatureWorkflow` runs the `analyst` agent
([`agents/agents/analyst.md`](../../agents/agents/analyst.md)) in a read-only sandbox with model
access: `opencode run --agent analyst --format json "<case>"`. The workflow, not the agent, decides
what happens next, from a validated verdict:

| Verdict | What the workflow does |
| --- | --- |
| `pass` | Posts the acceptance criteria and estimate, then stops (later stages are not built). |
| `needs_clarification` | Posts the questions, waits up to 7 days for a reply on the ticket, then runs the analyst again with the answers. At most 4 rounds; the last round tells the model not to ask. |
| `reject` | Posts the reason and stops. |
| unusable answer | One retry that tells the model what was wrong; then asks a person to look. |
| not signed in | Says so on the ticket and stops. |

`VerdictParser` accepts JSON wrapped in prose or code fences but is strict about the shape (allowed
verdicts, size limits, the complexity fields). `OpenCodeEvents` extracts the answer from OpenCode's
event stream. Ticket text and human replies reach the model only between markers, clipped and
labelled as data (`AnalystPrompt`), and model text is made inert before it is posted (`TicketComments`).

### Coverage check

When the analyst passes, the `coverage` agent ([`agents/agents/coverage.md`](../../agents/agents/coverage.md))
judges each acceptance criterion against the existing code in a read-only sandbox, so nothing is
built twice. It answers `covered`, `partial` or `missing` per criterion with up to three evidence
files. Three things keep that honest:

- `CoverageParser` requires exactly one entry per criterion and rejects evidence paths that are
  absolute, contain `..`, or use unusual characters.
- The overall verdict must agree with the statuses; it is derived from them
  (`CoverageVerdicts.Derive`), not taken on trust.
- The workflow checks the cited files exist in the workspace (`EvidenceCheck`; paths are passed to
  the shell as arguments, never as script text). A `covered` claim without an existing file
  becomes `partial`, a `partial` one `missing`. If the check cannot run, nothing counts as verified,
  so a failed check can never claim something is already built.

If every criterion is covered the workflow stops with `already_implemented`; otherwise it goes on
with the remaining criteria.

### Triage

`Triage.Decide` decides, with fixed rules and no model, whether a design review (the architect) comes
before building. It asks for one if any of these hold, and the comment on the ticket lists the reasons:

- the ticket has the `needs-architecture` label,
- the analyst estimates more than 5 files,
- more than one project, a public API change, a data model change, or a new dependency.

Otherwise the change is "small" and goes straight on. The architect and later stages are not
built yet, so the workflow ends after triage with status `triaged` and `NeedsArchitect` in its result.

### Workspace and sandbox check

When a feature starts, the controller clones the repository into
`FACTORY_WORKSPACE_ROOT/<workflow id>` on a branch `factory/issue-<number>` (`GitWorkspace`; the
token goes to git through the environment, never into arguments or the repository config). It
then asks the [sandbox launcher](../sandbox-launcher/README.md) to run `git log -1` against the
checkout in a `dotnet` sandbox, reports the result on the ticket, and removes the checkout when
the workflow ends, including when the ticket is closed.

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
