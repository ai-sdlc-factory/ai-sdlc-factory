# Controller

[Documentation index](../../docs/README.md) | [Workflows](../../docs/workflows.md) | [Architecture](../../docs/architecture.md) | [Deployment](../../deploy/README.md)

The trusted core of the factory: a standalone Temporal worker (.NET 10) that runs all the workflows, decides every next step in code, holds the only git host tokens, and runs git on
the pristine clone of each ticket. It exposes no HTTP port and does not reference the API project.

What the workflows do for a ticket is described in the [workflows guide](../../docs/workflows.md) (with diagrams); this page is about the service itself.

## What it runs

| Workflow | Started by | Does |
| --- | --- | --- |
| `RepoEventWorkflow` | the api, one per webhook delivery | records a receipt and **routes**: starts the ticket's workflow, signals a waiting one, or ignores the event ([routing](../../docs/workflows.md#ticket-routing)) |
| `FeatureWorkflow` | a `feature` ticket | [feature workflow](../../docs/workflows.md#feature-workflow) |
| `BugWorkflow` | a `bug` ticket | [bug workflow](../../docs/workflows.md#bug-workflow) |
| `IncidentWorkflow` | an `incident` ticket | [incident workflow](../../docs/workflows.md#incident-workflow) |
| `PlanWorkflow` | a `plan` ticket | [plan workflow](../../docs/workflows.md#plan-workflow) |
| `PullRequestRevisionWorkflow` | a review that requests changes, or `/fix`, on a factory pull request | [pull request revision](../../docs/workflows.md#pull-request-revisions): one new commit on the same branch |
| `DocsWorkflow` | a `docs` ticket | [documentation run](../../docs/workflows.md#documentation-run): `AGENTS.md`, `README.md` and `docs/` written from the code, as a pull request |
| `PullRequestUpdateWorkflow` | `/update` on a ticket or a factory pull request | [update](../../docs/workflows.md#updating-a-pull-request): the base branch merged in, conflicts resolved, one merge commit |
| `QaWorkflow` | `/qa` on a ticket or a factory pull request | [QA run](../../docs/workflows.md#qa-run): end-to-end tests written and run, a report on the ticket |
| `PullRequestReviewWorkflow` | `/review` on any pull request | [review](../../docs/workflows.md#reviewing-a-pull-request): the reviewers' findings posted, nothing written |
| `ImprovementWorkflow` | once a week, by `ImprovementService` | [weekly report](../../docs/operations.md#the-weekly-report-what-repeats) on what repeated, and with a repository set a ticket with an agent's proposals (never applied) |

All of them but the router share one base class, `FactoryWorkflowBase`: the workspace, the conversation with people, running one agent, the build loop with the project's checks and
reviewers, token limits, saving unfinished work and the run log. Each concrete workflow only defines its own stage order. Two hosted services run beside the worker: the janitor (`Janitor/`) and `ImprovementService`.

Activities (all in this service, except `sandbox_run`) are in `Workspace/WorkspaceActivities.cs` (`workspace_*`: checkouts, changes, publish, merge, the usage and resume files, the weekly report) and `GitHost/GitHostActivities.cs` (`githost_*`: comments, tickets, pull requests and what to read from them, through the `IGitHost` port). The workflows call `sandbox_run` on the
`sandbox` task queue, which the [sandbox launcher](../sandbox-launcher/README.md) serves. The controller itself polls `factory-events`.

## Run

Complete the [getting-started guide](../../docs/getting-started.md), then from `deploy/`:

```bash
docker compose up -d --build controller
docker compose logs -f controller
```

Settings (full list in the [configuration reference](../../docs/configuration.md#controller)): `TEMPORAL_ADDRESS`, `TEMPORAL_NAMESPACE`, `TEMPORAL_TASK_QUEUE` (the api and controller must agree on the
namespace and queue), `FACTORY_HOSTS` (each git host, with the bot's token on it), `FACTORY_WORKSPACE_ROOT`, `TEMPORAL_UI_URL`, and `FACTORY_AGENTS_DIR`. The same `FACTORY_HOSTS` carries the webhook secrets, which only the api needs; the controller ignores them. Without a bot token, ticket workflows fail
at their first comment and every other event still works.

The controller talks to a git host only through the `IGitHost` port (one client per kind of host: `ForgejoClient`, `GitHubClient`), and to the Docker world only through Temporal.

## Code layout

This folder (`src/controller/`) is grouped by concern. Everything is in the one namespace `DevFoundry.Controller`, and the tests follow the same folders.

| Folder | What is in it |
| --- | --- |
| `Workflows/Base/` | `FactoryWorkflowBase` (what every ticket workflow shares, in `.Stages`, `.Agent`, `.Build`, `.Record`, `.Bug`, `.Limits` and other parts), the result and status types (`WorkResult`, `Statuses`, `WorkContext`) and the activity and failure policies |
| `Workflows/Tickets/` | the workflows a label starts: `FeatureWorkflow`, `BugWorkflow`, `IncidentWorkflow`, `PlanWorkflow`, `DocsWorkflow`, each with its own stage order |
| `Workflows/PullRequests/` | the workflows a comment or review starts on a pull request: `PullRequestRevisionWorkflow`, `PullRequestUpdateWorkflow`, `PullRequestReviewWorkflow`, `QaWorkflow` |
| `Workflows/` | the rest: `RepoEventWorkflow` (every delivery), `AskWorkflow`, `SetupWorkflow` and `ImprovementWorkflow`; `Routing/` has `EventRouter`, `WorkMode` and the comment commands, `Requests/` the request types and the workflow id rules, `Comments/` the texts the factory posts on tickets (`TicketComments`) |
| `Agents/<role>/` | per role: its prompt builder and the parser that validates its answer (`Analyst`, `Coverage` with triage and the evidence check, `Architect`, `Developer` with the build feedback and pull request text, `Review` with the acceptance check and the UX decision, `Bug`, `Incident`, `Plan`, `Revision`, `Qa`, `Update`, `Docs`); `Agents/Common/` has what every role uses: the answer and JSON readers, the OpenCode event reader (answers and token usage), `PromptText` and `AgentCatalog` (model per role) |
| `Workspace/Git/` | `GitWorkspace` (the checkout of a run, in `.Changes`, `.Publish`, `.Record`, `.Process`, `.Merge`, `.Usage` and `.Resume` parts) |
| `Workspace/Checks/` | the change checks: `ProtectedPaths`, `SecretScan` and the change models; `Workspace/` itself has the workspace activities and the run record |
| `GitHost/` | the git host port (`IGitHost`), the router that sends a call to the host an owner belongs to, `GitHostKinds`, the host settings, the activities and the models it returns; `Forgejo/` and `GitHub/` inside it are the implementations of that port, the API clients |
| `Project/` | `.factory/project.json` parsing, the known keys (`ProjectKeys`), limits, rules and the `verify` settings |
| `Improvement/` | the weekly report: reading the logs, the rules that flag what repeats, the improver's answer and prompt, the scheduler |
| `Janitor/` | removing leftover checkouts and reporting old work-in-progress branches |

## Adding or changing things

**A new agent role** (a read-only analyst-style role is the simplest):

1. Write `agents/agents/<role>.md` (front matter with model and permissions, the prompt, the **limits** the parser enforces, and the answer shape). A limit the model was not told about is a limit it will break.
2. Add a record, a strict parser (use `AnswerReader` and `JsonFields`) and a prompt builder under `Agents/<role>/`; untrusted text goes through `PromptText.Clip` between markers. Add the comment texts and make model text inert with `TicketComments.Inert`.
3. Call it from a workflow with `ConverseAsync` (a role that may ask people) or `RunAgentAsync`. Anything the agent claims that can be checked should be checked in code. Pick its network profile in `NetworkFor` only if it needs more than `model`.
4. Test it: parser and prompt unit tests, and a scenario test with scripted agents (`FakeWorld`, `Answers`). Break the rule on purpose once to see the right test fail.
5. Document it: the role table in [configuration](../../docs/configuration.md#agents-and-models), the stage in [workflows](../../docs/workflows.md), and any new result status. `python3 tools/check-docs.py` fails until the role and statuses are listed.

**A new workflow kind** extends `FactoryWorkflowBase`, defines its signals (when it waits for people; the revision workflow does not) (`human_reply`, `ticket_closed`) and a `RunAsync` that calls `RunPipelineAsync`, and is registered in `ServiceRegistration.cs`, `EventRouter` (the label and the
`WorkIds` kind) and `RepoEventWorkflow` (start and signal).

## Tests

From the repository root:

```bash
docker build --target test -f src/controller/Dockerfile -t dev-foundry/controller-test .
docker run --rm dev-foundry/controller-test
```

The unit tests cover parsers, prompts, routing, the change checks and the git handling (including a planted hostile hook). The **scenario tests** run the real workflows on a Temporal dev server with scripted agents
and fake activities, so loops, caps, stops, comments and rescue are tested without a model; they skip themselves where no dev server is available. The tests mirror the source folders; `Scenarios/` holds the workflow tests.
