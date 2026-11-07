# Controller

[Documentation index](../../docs/README.md) | [Workflows](../../docs/workflows.md) | [Architecture](../../docs/architecture.md) | [Infrastructure](../../infra/README.md)

The trusted core of the factory: a standalone Temporal worker (.NET 10) that runs all the workflows, decides every next step in code, holds the only Forgejo token, and runs git on
the pristine clone of each ticket. It exposes no HTTP port and does not reference the API project.

What the workflows do for a ticket is described in the [workflows guide](../../docs/workflows.md) (with diagrams); this page is about the service itself.

## What it runs

| Workflow | Started by | Does |
| --- | --- | --- |
| `ForgejoEventWorkflow` | the api, one per webhook delivery | records a receipt and **routes**: starts the ticket's workflow, signals a waiting one, or ignores the event ([routing](../../docs/workflows.md#ticket-routing)) |
| `FeatureWorkflow` | a `feature` ticket | [feature workflow](../../docs/workflows.md#feature-workflow) |
| `BugWorkflow` | a `bug` ticket | [bug workflow](../../docs/workflows.md#bug-workflow) |
| `IncidentWorkflow` | an `incident` ticket | [incident workflow](../../docs/workflows.md#incident-workflow) |
| `PullRequestRevisionWorkflow` | a review that requests changes, or `/fix`, on a factory pull request | [pull request revision](../../docs/workflows.md#pull-request-revisions): one new commit on the same branch |

The three ticket workflows share one base class, `FactoryWorkflowBase`: the workspace, the conversation with people, running one agent, the build loop with the project's checks and
reviewers, and saving unfinished work. Each concrete workflow only defines its own stage order.

Activities (all in this service, except `sandbox_run`): `workspace_prepare`, `workspace_cleanup`, `workspace_read_project_config`, `workspace_changes`, `workspace_diff`, `workspace_rescue`,
`workspace_publish`, `workspace_record`, `forgejo_post_issue_comment`, `forgejo_post_notice_once`, `forgejo_create_issue`, `forgejo_create_pull_request`, `forgejo_read_revision_context`. The workflows call `sandbox_run` on the
`sandbox` task queue, which the [sandbox launcher](../sandbox-launcher/README.md) serves. The controller itself polls `factory-events`.

## Run

Complete the [getting-started guide](../../docs/getting-started.md), then from `infra/`:

```bash
docker compose up -d --build controller
docker compose logs -f controller
```

Settings (full list in the [configuration reference](../../docs/configuration.md#controller)): `TEMPORAL_ADDRESS`, `TEMPORAL_NAMESPACE`, `TEMPORAL_TASK_QUEUE` (the api and controller must agree on the
namespace and queue), `FORGEJO_BOT_TOKEN`, `FACTORY_WORKSPACE_ROOT`, `FORGEJO_URL`, `TEMPORAL_UI_URL`, and `FACTORY_AGENTS_DIR`. The controller does **not** receive the webhook secret. Without a bot token, ticket workflows fail
at their first comment and every other event still works.

The controller talks to Forgejo only through `ForgejoClient`, and to the Docker world only through Temporal.

## Code layout

`src/DevFoundry.Controller/` is grouped by concern. Everything is in the one namespace `DevFoundry.Controller`, and the tests follow the same folders.

| Folder | What is in it |
| --- | --- |
| `Workflows/` | `FactoryWorkflowBase` (what every ticket workflow shares, in `.Stages`, `.Agent`, `.Build`, `.Record`, `.Bug` parts), `FeatureWorkflow`, `BugWorkflow`, `IncidentWorkflow` and `PullRequestRevisionWorkflow` (their own stage orders), `ForgejoEventWorkflow`, `EventRouter`, `WorkMode`, `IncidentCommands`, `WorkRequest` and the workflow id rules |
| `Agents/<role>/` | per role: its prompt builder and the parser that validates its answer (`Analyst`, `Coverage` with triage and the evidence check, `Architect`, `Developer` with the build feedback and pull request text, `Review` with QA and the UX decision, `Bug`, `Incident`, `Revision`) |
| `Workspace/` | `GitWorkspace` (`.Changes`, `.Publish`, `.Record`, `.Process` parts), its activities, the change checks (`ProtectedPaths`, `SecretScan`) and the run record |
| `Forgejo/` | the API client, the comment, ticket and pull request activities, ticket comment texts |
| `Project/` | `.factory/project.json` parsing |
| `Common/` | JSON helpers, the OpenCode event reader (answers and token usage), `AgentCatalog` (model per role) |

## Adding or changing things

**A new agent role** (a read-only analyst-style role is the simplest):

1. Write `agents/agents/<role>.md` (front matter with model and permissions, the prompt, the **limits** the parser enforces, and the answer shape). A limit the model was not told about is a limit it will break.
2. Add a record, a strict parser (use `AnswerReader` and `JsonFields`) and a prompt builder under `Agents/<role>/`; untrusted text goes through `PromptText.Clip` between markers. Add the comment texts and make model text inert with `TicketComments.Inert`.
3. Call it from a workflow with `ConverseAsync` (a role that may ask people) or `RunAgentAsync`. Anything the agent claims that can be checked should be checked in code. Pick its network profile in `NetworkFor` only if it needs more than `model`.
4. Test it: parser and prompt unit tests, and a scenario test with scripted agents (`FakeWorld`, `Answers`). Break the rule on purpose once to see the right test fail.
5. Document it: the role table in [configuration](../../docs/configuration.md#agents-and-models), the stage in [workflows](../../docs/workflows.md), and any new result status. `python3 tools/check-docs.py` fails until the role and statuses are listed.

**A new workflow kind** extends `FactoryWorkflowBase`, defines its signals (when it waits for people; the revision workflow does not) (`human_reply`, `ticket_closed`) and a `RunAsync` that calls `RunPipelineAsync`, and is registered in `Program.cs`, `EventRouter` (the label and the
`WorkIds` kind) and `ForgejoEventWorkflow` (start and signal).

## Tests

From the repository root:

```bash
docker build --target test -f services/controller/Dockerfile -t dev-foundry/controller-test .
docker run --rm dev-foundry/controller-test
```

The unit tests cover parsers, prompts, routing, the change checks and the git handling (including a planted hostile hook). The **scenario tests** run the real workflows on a Temporal dev server with scripted agents
and fake activities, so loops, caps, stops, comments and rescue are tested without a model; they skip themselves where no dev server is available. The tests mirror the source folders; `Scenarios/` holds the workflow tests.
