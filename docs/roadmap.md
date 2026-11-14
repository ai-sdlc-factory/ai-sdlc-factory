# Roadmap and ideas

[Documentation index](README.md) | [Design and decisions](design.md#not-built-yet)

A personal to-do list of what is **not built yet**, so ideas are not forgotten. Tick an item when it is done and move it to the "Done" section at the bottom. The reasoning behind the bigger
items is in [Design and decisions](design.md#not-built-yet); how the built parts work is in [Workflows](workflows.md).

Effort is a rough guess: **S** about a day, **M** a few days, **L** a week or more.

## Your list

- [x] **Demo service with monitoring and deployment** (done as a first version: [`demo/`](../demo/README.md)). The weather application runs from its default branch in Docker, with Prometheus, Alertmanager, Grafana and Loki, a delivery script with rollback and a script that breaks it on purpose. Still open: let a merge to the default branch start the delivery (a Forgejo Actions runner or a webhook), and blue/green switching.
- [x] **Alert intake: react to monitoring alerts** (done as a first version: [`services/alert-intake`](../services/alert-intake/README.md)). Alertmanager notifications become one ticket per alert, with an operator-owned route table, deduplication by fingerprint, a rate limit, redacted and bounded evidence (recent log lines from Loki), and a dry run as the default. Still open: more sources than Alertmanager, closing tickets by itself when an alert resolves (today it only comments), and running it in the factory's own Compose stack.
- [ ] **Factory improvement loop** (M). Periodically evaluate what happened and what could be improved. Needs a durable run log first (one line per finished run, no ticket text). Then a weekly deterministic report of what repeats (rejected answers, rounds used, refused hosts, token outliers),
  and an agent that proposes prompt and config changes as a reviewed ticket, never applied automatically.
- [ ] **Scheduled UI check** (L). A separate scheduled flow that checks the real UI, for projects that have one, and files bug tickets for what is broken or looks wrong (or becomes part of QA/UX).
  Design: a Temporal schedule per project (operator-owned list), a browser sandbox, deterministic findings (console errors, accessibility violations, screenshots), reproduced twice before a ticket is filed, fingerprint deduplication, a cap on new tickets per sweep,
  and tickets labelled `auto-detected` that a person promotes to `bug` (or a per-project auto-start). Depends on the browser image below. The same frame can later run other sweeps (dependency audit, broken links, flaky tests on `main`).

## Suggested workflows

| # | Workflow | What it does | Why it fits | Effort | Done |
| --- | --- | --- | --- | --- | --- |
| 1 | PR revision loop | A human review comment (or "request changes") on a factory PR starts a bounded revision: the developer addresses it, the checks and reviewers run again, and a new commit goes to the same branch | It completes the loop after the PR is opened. Today a person has to fix the PR by hand | Medium | [x] |
| 2 | Review of human PRs | When a person opens a PR, the reviewer and security roles review it read-only and post findings | The roles and the diff handling already exist. Nothing is written, so it is low risk | Small | [ ] |
| 3 | Dependency updates | A scheduled run bumps dependencies, runs the project's checks, and opens a PR with the changes and test results | Temporal schedules suit this. It is repetitive, the checks decide, and humans review | Medium | [ ] |
| 4 | Planner | A `plan` ticket is split into proposed feature tickets that a person approves | Helps with big goals, but a wrong split costs a lot, so it needs approval | Medium | [x] |
| 5 | CI-failure triage | When the pipeline fails on a branch, diagnose and propose or open a fix | It reuses the bug workflow, but needs a webhook from Bitbucket Pipelines or Forgejo CI | Medium | [ ] |
| 6 | Docs and `AGENTS.md` upkeep | Onboarding draft for a new repo, and updates when the structure changes | Cheap, and it improves every other workflow's input | Small | [ ] |
| 7 | Release notes | A changelog draft from merged PRs | Useful but low risk and low priority | Small | [ ] |
| 8 | Factory retrospective | A weekly report of repeated problems, from a durable run log | Needs the run log first | Medium | [ ] |

Item 8 is also on your list above (the "factory improvement loop"); items 1 and 4 are done.

## Next up (from building `/retry`, the failure classes and `/qa`)

Small gaps found while building and testing these. None is started.

- [ ] **Who may start things** (S). Anyone who can comment can start `/qa`, `/fix`, `/update` and `/retry`, and each spends tokens. Check write access to the repository first (the `permissions.push` Forgejo already reports for the bot's own repositories is not enough: it must be the commenter's).
- [ ] **A durable usage log** (S to M). Token use is now in every workflow result (`usage`) and in the run record, but there is no total across tickets, days or repositories. Append one line per finished run (workflow id, kind, repository, tokens) on the host; it is also the base for the budget item below and for the improvement loop.
- [ ] **QA: tell the tester what the pull request changed** (S). Give it the list of changed files (Forgejo `pulls/<n>/files`) and the diff, not only the title and criteria.
- [ ] **QA: notice a UI-only change with no `ui` suite before spending a tester run** (S). Compare the changed files with the project's `ui` paths and say so on the ticket. The same comment should also show the scope line, which the "blocked" comment lacks today.
- [ ] **QA: keep the tests that expose a real bug** (S). A failing run saves its tests to a `-wip` branch, so the developer fixing the findings never sees the test. Show it the failing output in `/fix qa`, or push the tests in a separate commit.
- [ ] **QA: services for the application** (M). The sandbox has no database or cache server, so a QA run needs an application that works with SQLite or in-memory fakes. Sidecar containers on an isolated network would need an image allowlist like the repository-image policy.
- [ ] **QA: screenshots for the UX review** (S to find out). Today it reads page text and accessibility snapshots. Find out whether OpenCode with the ChatGPT login accepts an image as input.
- [ ] **`/retry` that resumes** (M). It starts the run again from the beginning. Resuming from the last good stage needs stage results saved between runs. Infrastructure failures are deliberately not retried by themselves, because a second try can spend tokens twice.
- [ ] **Rename the feature workflow's `qa` role to `acceptance`** (S). It reads the diff against the acceptance criteria and never runs the application, which the `/qa` workflow now does; two things called QA confuse people.

## Other ideas we discussed

- [x] **Browser image and end-to-end spike** (done: the `browser` image runs Chromium in the locked-down sandbox, see [QA run](workflows.md#qa-run); whether OpenCode can take a screenshot as input is still open, so the UX review reads page text and accessibility snapshots). Original item (S). A sandbox image with Chromium and Playwright, so a project can run browser tests as a gate. Spike on the fixture's `web/` folder first: memory use inside the 2 GB sandbox, whether Chromium launches with all capabilities dropped,
  and whether OpenCode can take a screenshot as input. Prerequisite for the scheduled UI check and for UI bugs that can be reproduced with a test.
- [ ] **Token and spending limits** (M). The per-run total is stored now (see "A durable usage log" above). A per-ticket budget first (a project can only lower a factory-wide maximum), then daily and per-repository caps from a host-side usage log, and recognition of the provider's usage-limit errors so a run stops cleanly.
- [ ] **Warn on unknown keys in `.factory/project.json`** (S). Today a misspelled optional key is silently ignored.
- [ ] **Startup check of each role's model** (S). Fail early when a role names a model the login cannot use.
- [ ] **Models and reasoning per role** (S). Try `variant` (low for the roles the controller checks mechanically, high for developer, architect and reviewers) and compare tokens and quality from the run records.
- [ ] **Repo map or language-server tools** (M). A generated repository map (ctags) at sandbox start, or language servers baked into the images, for large repositories.
- [ ] **Marketing visuals** (S). Two non-technical images for slides and marketing: the ticket-to-reviewed-code flow, and a simplified architecture. A first draft of the flow image had card text running to the bottom edge and an overflowing label, and the drafts are gone, so they need to be redone before review.

## Done

- [x] **Planner agent and workflow**: a `plan` ticket is split by the `planner` role into proposed feature tickets, which are created only when a person writes `/approve` (see [Plan workflow](workflows.md#plan-workflow)). Still open: tickets that wait for another are created only when a person names them, because the factory does not react to merges; a way to start the next tickets by itself when a merge happens. Needs a scenario run on a machine with the Temporal dev server before it is trusted.
- [x] `verify` in the templates: the starter projects have every feature on, including end-to-end suites, and their agent instructions say e2e belongs to the `/qa` run.
- [x] `/update`: the base branch is merged into a factory pull request (on the ticket or on the pull request), conflicts are resolved by the `merge-resolver` role, the checks and reviewers run on the result, and one merge commit is pushed (see [Updating a pull request](workflows.md#updating-a-pull-request)). Still open: a person can start it by asking, but nothing starts it when the base moves; and a conflict in a protected file, or in more than 40 files, is left to a person.
- [x] Revisions and updates give the next round's reviewers the earlier findings to check, as the build loop does.
- [x] `/retry`, and failures of the factory's own machinery told apart from the ticket's (`infrastructure_failed`).
- [x] Build rounds announce themselves, the developer's report is posted, and reviewers check the earlier findings.
- [x] `/qa` workflow: end-to-end API and UI tests of a finished pull request (`/qa`, `/qa full`, `/qa <note>`, `/fix qa`), the `browser` sandbox image, and the token use of every run stored in its result.

- [x] Janitor: removes checkouts of finished runs, lists (and optionally deletes) old work-in-progress branches, and removes old unused repository images.
- [x] Feature workflow: analyst, coverage check, triage, architect, developer, project checks, reviewers, UX review, QA, pull request.
- [x] Bug workflow with the red-then-green proof.
- [x] Incident workflow, advice only, with `/option`, `/hotfix` and `/resolved` and a draft postmortem.
- [x] Quick mode, and notices for comments that cannot be used.
- [x] Pull request revisions: a review that requests changes, or `/fix`, on a factory pull request becomes one new commit on the same branch.
- [x] Run record with model, tokens and cost; work-in-progress branch for runs that stop.
- [x] Egress allowlist with `enforce`, `audit` and `open` modes; per-role models; sandbox images for .NET, Python and Node.
- [x] Documentation, diagrams and the documentation check.
