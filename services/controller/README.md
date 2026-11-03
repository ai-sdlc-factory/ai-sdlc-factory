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

## Feature tickets

`ForgejoEventWorkflow` also routes Forgejo ticket events (decisions live in `EventRouter`):

- An open ticket labeled `feature` (on `issues` opened, reopened or label updated) starts
  `FeatureWorkflow` with the ID `feature-<owner>-<repo>-<number>-<hash>`, where the hash (of the exact owner and repository) keeps repositories with similar names, and very long names, apart. The same name is the ticket's workspace folder. Repeated events for the same
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

### Architect

Only when triage asks for it, the `architect` agent ([`agents/agents/architect.md`](../../agents/agents/architect.md))
designs the change in a read-only sandbox. It gets the criteria that still have to be met (the ones the
coverage check did not find satisfied) and triage's reasons. Its validated answer
(`ArchitectParser`) holds an approach, the components that change (safe relative paths and what changes
in each), risks, and guidance for the build; the comment on the ticket shows all of it.

| Verdict | What the workflow does |
| --- | --- |
| `pass` | Posts the design and continues (status `designed` for now, because the build is not built yet). |
| `needs_decision` | Posts the questions, waits for a reply on the ticket, runs the architect again with the answers. At most 3 rounds; the last round forbids questions. |
| `reject` | Posts why the change is not feasible as asked and stops (`design_rejected`). |

The analyst and the architect share one conversation loop (`ConverseAsync`): run the agent, post the
answer, then continue, stop, or ask and wait. Code maps the verdict to the next step.

### Build and pull request

After the design (or straight after triage for a small change) the build runs, up to 3 rounds:

1. The `developer` agent ([`agents/agents/developer.md`](../../agents/agents/developer.md)) works in the
   **writable** workspace with a shell. It does not commit or push.
2. Fixed checks look at what changed (`GitWorkspace.DetectChangesAsync`): protected paths
   (`.factory/`, `.github/`, `.forgejo/`, CI files), symbolic links, file size and count, and a secret
   scan. `.gitignore` decides what counts as noise. Violations go back to the developer as feedback.
3. The project's own checks (gates) run one by one in the sandbox **without** model access.
   Failures go back to the developer with the end of their output.
4. The reviewers and QA look at the change (see below). Blocking findings or unmet criteria go back to
   the developer like a failed check and use up one of the three rounds.
5. When everything passes, the factory publishes. `PublishAsync` repeats the checks, applies only the
   verified files onto the pristine clone, commits, pushes `factory/issue-<n>`, and `ForgejoClient`
   opens the pull request (`Closes #<n>`). A person reviews and merges; the factory never merges.

### Review and QA

After the automated checks pass, the project\'s reviewers and then QA examine the change:

- **Reviewers** (`reviewer`, and `security` if the project lists it) answer with findings, each a
  `blocker`, `major`, `minor` or `nit`. Any blocker or major finding means `needs_changes`; `ReviewParser`
  derives the verdict from the findings and refuses an answer where the two disagree. Minor findings and
  nits do not block; they are listed in the pull request.
- **QA** judges every remaining acceptance criterion as `met` or `not_met`. A `met` criterion must cite
  files, and the controller checks they exist; one that does not counts as `not_met`.
- The reviewing agents have **no shell and a read-only workspace**. The controller produces the diff
  (`GitWorkspace.DiffAsync`, with git running only in the trusted directory) and puts it in the prompt, so
  the agents that judge the work cannot run commands while the OpenCode login is readable.
- A review or QA answer that stays unusable after the retry stops the run (`review_failed`): nothing is
  published unreviewed.

Which reviewers run is a per-project choice in `.factory/project.json`: `"reviewers": ["reviewer", "security"]`
(default `["reviewer"]`; QA always runs).

**UX review.** A `ux` reviewer ([`agents/agents/ux.md`](../../agents/agents/ux.md)) runs between the
reviewers and QA, but only for a project that has a user interface and only when the UI change is medium or
large. It looks at usability (flows, states such as loading, empty and error, wording), accessibility visible in
the code (labels, keyboard use, contrast, semantic elements) and consistency with the existing components and
styles. It cannot run or see the interface, so it judges from the code and says when something needs to be seen.
The decision is made by counting, not by an agent (`UiChange`):

- The project opts in with `"ui"` in `.factory/project.json`; without it no UX review ever runs. `paths` lists
  the UI files (see the patterns below).
- Only changed files that match `paths` count. A change is **medium** when it touches at least `medium.files`
  UI files or changes at least `medium.lines` lines in them (defaults 3 files or 40 lines), and **large** from
  `large.files` or `large.lines` (defaults 8 or 200). Either number is enough. A **small** change, or none in
  the UI at all, gets no UX review, and the pull request says which: "no UI files changed" or the numbers.
- When it runs, the UX reviewer is given only the UI files and their diff (and how many other files changed).
  Its findings use the same severities as the other reviewers: a blocker or major finding goes back to the
  developer, and the review step is shared with the other reviewers, QA and the three-round cap.
- A diff too long to be shown whole, or a large new file, counts as large rather than being under-counted.

Patterns: `*` matches within one folder, `**` across folders (`**/` also matches none), `?` one character; a
pattern without a slash is matched against the file name wherever it is, so `*.css` means every stylesheet.

Three rounds that fail leave the ticket with a comment and nothing published as a pull request. A developer
that answers `blocked` (for example because the ticket needs a protected file changed) stops the run with its
reasons. In both cases the work so far is saved to a branch (below).

### When a run stops: the work-in-progress branch

A run that stops without a pull request would otherwise lose everything the developer wrote, because the
checkout is deleted when the workflow ends. So before that happens the controller saves the work:

- **When:** after `build_failed`, `build_blocked`, `review_failed`, `publish_failed`, a lost login (`blocked`) and
  any unexpected error (`error`, which now also gets a comment instead of silence). Not when a person closed the
  ticket, and not for runs that stopped before the build, which have no code.
- **Where:** a branch `factory/issue-<n>-wip` (or `-wip-2`, `-wip-3` if one is already there; an earlier branch is
  never overwritten), with one commit `WIP: <title> (#<n>), not finished`. No pull request is opened.
- **What:** the same trusted path as a publish (files are copied onto the pristine clone), but the checks do not
  have to pass. Only a file with a problem of its own is left out and listed in the comment: a protected file, a
  symbolic link, a file over 2 MiB, or one the secret scan flags. If more than 300 files changed (usually build
  output) or the total is too big, nothing is saved and the comment says why. Credentials are never pushed.
- **The comment** on the ticket says why the run stopped, which branch holds the work and that it is not
  reviewed, what was left out, and a link to the run in Temporal, where every prompt, agent answer and check
  output can be read. Temporal keeps that for a limited time (`TEMPORAL_RETENTION`, 7 days by default), which the
  comment mentions; `TEMPORAL_UI_URL` (default `http://localhost:8080`) sets the address used in the link.
- When only opening the pull request fails after the branch was published, the comment points at that branch.
- Saving is best effort: if it fails, the ticket says so and the run keeps its real outcome.

Publishing and saving can be repeated safely: the pristine clone is reset to its starting commit before every
inspection, a branch that already holds exactly the same content is reused, and a re-run on a ticket whose branch
exists gets the next free name (`factory/issue-<n>-2`) instead of a rejected push.

**Why there are two copies of the checkout.** The agent can write anything into its workspace, including
`.git/hooks` and `.git/config` entries that run commands the next time git runs there. Git therefore
never runs in the agent's copy. Intake clones into `<root>/.base/<name>` (no sandbox mounts it) and
copies that to `<root>/<name>` for the agents. Changes are read from the agent's tree as plain files
through git's trusted directory, and published by copying regular files onto the pristine clone.
`WorkspaceChangesTests` plants a hostile hook and config and proves nothing runs.

### Network access

Every sandbox run asks the launcher for an egress profile, chosen by role in code (`NetworkFor`) and never
by the agent or the repository: read-only agents (analyst, coverage, architect, reviewers, QA) get `model`
(the model provider only), the developer gets `developer` (model provider and package registries), the
project\'s checks get `packages` (registries only, no model), and the evidence check gets no network at
all. The hosts behind each profile are in [`egress/profiles.json`](../../egress/README.md).

### Project configuration

A repository describes itself in `.factory/project.json` on its default branch, read from the pristine clone:

```json
{
  "image": "python",
  "gates": [
    { "name": "test", "command": ["python", "-m", "pytest", "-q"], "timeout_seconds": 300 }
  ]
}
```

Optional: `"reviewers": ["reviewer", "security"]` to add the security reviewer, and for a project with a user
interface:

```json
"ui": {
  "paths": ["web/**", "*.css", "src/**/*.tsx"],
  "medium": { "files": 3, "lines": 40 },
  "large": { "files": 8, "lines": 200 }
}
```

`medium` and `large` can be left out (the values above are the defaults). Both are about changes in the files
matching `paths` only; a UX review runs from medium upwards.

`image` must be an alias the sandbox launcher allows (`dotnet`, `python`, `node`). A gate is an argument list
(no shell; use `["sh", "-c", "..."]` if you need one), 1 to 8 gates, timeout 10 to 1800 seconds (default
600). The file is untrusted input: it can only choose among the images the factory offers, and the
commands run in the sandbox the agent already works in. Without it the run stops with an example.

### Limits and error messages

Every agent answer is validated against explicit limits (for example the architect: at most 30
components, 500 characters per `change`, 10 risks, 15 guidance items, 3 questions). Two rules keep
that workable for a model:

- The limits are stated in the role file (`agents/agents/<role>.md`), because a model cannot respect a
  limit it was never told.
- A rejection names the exact field, item and limit (`"components" has 31 entries, the limit is 30`),
  because that message is all the retry prompt can use.

When an answer is still unusable after the retry, the reason is posted on the ticket and logged by the
controller. The raw answer stays in the workflow's history in Temporal.

### Starting again

A finished ticket is started again by **closing and reopening it**: a `reopened` event starts a new
run, while other events for a ticket that already had a run are ignored.

### Workspace and sandbox check

When a feature starts, the controller clones the repository into
`FACTORY_WORKSPACE_ROOT/<workflow id>` on a branch `factory/issue-<number>` (`GitWorkspace`; the
token goes to git through the environment, never into arguments or the repository config). It
then asks the [sandbox launcher](../sandbox-launcher/README.md) to run `git log -1` against the
checkout in a `dotnet` sandbox, reports the result on the ticket, and removes the checkout when
the workflow ends, including when the ticket is closed.

## Workflow contract

The workflow input and the sandbox request and result are defined once in
[`libs/DevFoundry.Contracts`](../../libs/DevFoundry.Contracts/Contracts.cs), shared with the API
and the sandbox launcher. The contract tests below pin their JSON names.

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

## Starting from a pull request

The pull request body lists the criteria, the design and the checks that passed, and carries
`Closes #<n>`; merging it closes the ticket.

## Tests

From the repository root:

```bash
docker build --target test -f services/controller/Dockerfile -t dev-foundry/controller-test .
docker run --rm dev-foundry/controller-test
```

The xUnit tests cover three layers:

- **Units:** the JSON contract with the API, the router, every parser and validator, the prompts,
  the secret scan, and the workspace code against real git (including a hostile `.git` in the agent's copy).
- **Workflow scenarios** (`Scenarios/`): the real `FeatureWorkflow` and `ForgejoEventWorkflow` run on a
  Temporal dev server, with scripted agents, project checks, workspace and Forgejo. They exercise every
  branch a live run cannot be made to take: clarification rounds, closing a ticket while it waits,
  unusable and crashing agents, coverage claims without evidence, the architect asking or declining,
  rejected changes, failing checks and the three-round cap, a blocked developer, publish failures, and
  routing (reopening restarts a run, comments reach a waiting run, the factory ignores its own comments).
  No model or network is involved. The dev server is the `temporal` binary the stack already ships
  (copied into the test image), so the tests are skipped outside Docker. The 7-day waits cannot be
  tested this way.
- **Live runs** against the real stack, described in the guides above, for what only a real model shows.

See the [integration tests](../../tests/integration/README.md) for the full HTTP-to-workflow path.
