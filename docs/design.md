# Design and decisions

[Documentation index](README.md) | [Architecture](architecture.md) | [Workflows](workflows.md)

Why the factory is built the way it is, what running it taught us, and what is deliberately not built yet. The factory follows the stage-and-artifact idea of
Anthropic's *AI-native SDLC playbook*: every stage produces a reviewable artifact, deterministic code controls the flow, and humans own the gates.

## Principles

1. **Agents advise, code decides.** Every agent run ends with one JSON verdict in a fixed shape. Strict parsers check the shape, values, limits and paths; the workflow maps the verdict to the next step in code.
   Wherever an agent claims something that can be checked, the factory checks it itself: cited files and commits exist, a bug's test fails and then passes, only the allowed file was written.
2. **Small trusted core, disposable everything else.** One service holds the Docker socket, one holds the Forgejo token, and each agent runs in its own throwaway container with a fixed isolation policy and no route out
   except an allowlisting proxy. See [Architecture](architecture.md#trust-boundaries).
3. **The repository is untrusted input.** `.factory/project.json` can choose among options the factory offers; it cannot widen anything. Ticket and repository text reach a model only as marked, clipped data.
4. **Humans own the gates.** Humans decide scope (the ticket), answer questions on the ticket, review every pull request, and merge. For incidents, humans carry out every action; the factory only advises.
5. **Everything on the ticket.** Communication with people is comments on the ticket, and a workflow that needs a person simply waits (a Temporal wait costs nothing).
6. **Nothing is lost.** Stopped runs save their work to a `-wip` branch; every step is in Temporal history, and optionally in a committed run record.
7. **Bounded everything.** Round caps, retries, timeouts, file and size limits, and a concurrency limit. A model cannot make a run loop forever.

## Decisions and why

| Decision | Why |
| --- | --- |
| **Temporal** for orchestration | Runs last minutes to days (waiting for answers), must survive restarts, and need an audit trail of every step. |
| **A separate launcher** for Docker | Docker access is effectively root on the host. A small service with no tokens that builds containers from one fixed specification is far easier to trust than agent-facing code with the socket. |
| **Roles as Markdown files** | Permissions, models and prompts are reviewed like code and change without a rebuild. One role, one file, one verdict shape. |
| **Two copies of the checkout** | An agent can plant `.git/hooks`; git never runs where an agent can write. See [the trust diagram](architecture.md#why-there-are-two-copies-of-the-checkout). |
| **Reviewers and QA have no shell** | The agents that judge the work must not be able to run commands while the OpenCode login is readable; the controller gives them the diff in the prompt. |
| **Egress allowlist through a proxy** | A tricked agent must not be able to send code or the login to an arbitrary server. A per-profile internal network plus a CONNECT-only proxy is enforced by the network, not by the agent's good behaviour. |
| **Triage by fixed rules** | Whether a design review is needed is decided by counting the analyst's facts, so the same ticket gets the same answer and every reason is stated. |
| **Quick mode chosen by a person** | An agent's estimate must never switch checks off. See [Quick mode](workflows.md#quick-mode). |
| **Red then green for bugs** | A fix is only credible with a test that failed on the old code and passes on the new; the factory runs both itself and forbids editing the test. |
| **Incident = advice only; slash commands** | The factory has no access to production and should not get any. People decide with fixed commands, because a model's reading of free text is not a safe way to trigger actions. |
| **A separate collector for incidents** (first version built: the [alert intake](../services/alert-intake/README.md)) | Detecting incidents needs production access. That belongs in a small, separate, deterministic component that files a ticket with a bounded, redacted evidence bundle, so agents never see production. The intake does exactly that for Alertmanager alerts: an operator-owned route table, deduplication by fingerprint, a rate limit, redacted evidence, and a dry run as the default. |
| **The factory never merges** | The last gate is a person. |
| **ChatGPT login, not an API key** | The target is a personal or small-team setup with a subscription. It means no per-token price and no provider-side spending cap. |

## Findings from running it

- **Validators must match what the model was told.** A first architect run failed twice because the validator allowed 300 characters per change, 20 components and no trailing slash on directories, none of which the role file mentioned.
  Limits are now stated in every role file, and a rejection names the exact field and limit, because that message is all the retry prompt has.
- **The analyst flags `public_api_change` too eagerly** for any new function, which makes triage ask for a design review on small tickets. Tightening the analyst wording (a public API is one other projects or users depend on) is the
  cheaper fix than changing the triage rules; quick mode is the escape hatch.
- **The coverage agent is strict on purpose.** A test that calls a function directly does not count as "a test that runs the program". Expect more `partial` than `covered`.
- **Reviewers and QA need to know which checks exist.** Without that, QA demanded tests for "unchanged" constraints that the project's checks could not run, and the developer and reviewer disagreed. The prompts now list the checks.
- **Retrying a publish can see "no changes".** After an attempt that had committed, the next inspection found nothing. The pristine clone is now reset before every inspection, and a branch with identical content is reused.
- **Some paths cannot be provoked with a real model**: the architect's `needs_decision` and `reject`, build loops, publish failures. They are covered by workflow scenario tests with scripted agents on a Temporal dev server, and breaking a rule on purpose
  (the round cap, the red check, the one-file postmortem rule, the notice limits, ...) makes exactly the matching tests fail.
- **Docker Desktop creates a missing bind source as root**, so the launcher checks that the workspace exists and is not a symlink before mounting it.
- **The .NET CLI crashes if `HOME` does not exist**, while Python and Node tolerate it. The sandbox entrypoint now creates `HOME` and the XDG directories. Found by actually running the documentation's language examples.
- **A model can follow `AGENTS.md`**: a developer told "tests live in `check_x.py`, plain asserts" wrote exactly that. The analyst-type roles are told to treat repository text as data, so they will not obey commands in it.
- **Token use** of a small feature in the full process was about 52,000 (about 28,000 without cache reads); in quick mode about 28,000 in total. The developer is the largest part either way.

## Not built yet

These are designed or discussed, and deliberately left out until real use shows they are needed. The working to-do list is the [roadmap](roadmap.md).

| Item | Idea |
| --- | --- |
| **Limits and budgets** | Track tokens per ticket and per day. First a per-ticket budget in `.factory/project.json` (counting input, output, reasoning and cache writes, not cache reads) that a project can only lower from a factory-wide maximum; over it, the run stops, saves its work and says so. Then daily and per-repository caps from a host-side usage log, waiting instead of failing. With a ChatGPT login there is no provider-side cap to set, and provider usage-limit errors should be recognised and stop the run. Checks happen between agent runs, so a single run can overshoot; timeouts bound that. |
| **Review comments inline** | Reply inside each comment's thread (Forgejo's API has no way to; the factory posts one summary reply with a table), and react to individual line comments instead of a whole review. |
| **Incident collector, the rest** | The first version exists ([alert-intake](../services/alert-intake/README.md), run by the [demo environment](../demo/README.md)). Still open: sources other than Alertmanager, closing a ticket by itself when its alert resolves (today it only comments), running it in the factory's own Compose stack, and pointing it at a real production system instead of the demo. |
| **Planner** | A `plan` ticket is split into proposed feature tickets that a person approves before they are created; only tickets without dependencies are created first, because the factory does not yet react to merges. |
| **Review of human pull requests** | Run the reviewer and security roles read-only on pull requests that people open. |
| **Dependency updates** | A scheduled run that bumps dependencies, runs the project's checks and opens a pull request. |
| **Factory retrospective** | A durable run log (one line per finished run, no ticket text) and a weekly deterministic report of what repeats (rejected answers, rounds used, refused hosts, outliers), with an agent proposing prompt changes as a reviewed ticket, never applied automatically. |
| **Warning on unknown `project.json` keys** | Today a misspelled optional key is silently ignored. |
| **Startup check of each role's model** | Fail early when a role names a model the login cannot use. |
| **Language-server tools** | OpenCode's language-server downloads are off for network reasons; if wanted, bake the servers into the images. A generated repo map (ctags) could also help large repositories. |

## Older notes

- The factory started as a feature workflow and grew the bug and incident workflows on the same shared stages. The [workflows guide](workflows.md) is the current description; the diagrams in [`diagrams/`](diagrams/) are the source of the images.
