# Roadmap and ideas

[Documentation index](README.md) | [Design and decisions](design.md#not-built-yet)

A personal to-do list of what is **not built yet**, so ideas are not forgotten. Tick an item when it is done and move it to the "Done" section at the bottom. The reasoning behind the bigger
items is in [Design and decisions](design.md#not-built-yet); how the built parts work is in [Workflows](workflows.md).

Effort is a rough guess: **S** about a day, **M** a few days, **L** a week or more.

## Your list

- [ ] **Demo service with monitoring and deployment** (L). A small real service to run, deploy and monitor, so the incident side of the factory has something real to watch. Prerequisite for the next item.
- [ ] **Alert intake: react to monitoring alerts** (M). A place or small agent that receives monitoring alerts that need a reaction and files the ticket that starts the incident or bug workflow. Design: a separate, deterministic collector
  (signed alert endpoint, bounded and redacted evidence, deduplication by fingerprint, rate limit, operator-owned alert-to-repository mapping) so agents never get production access.
  See [the incident collector](design.md#not-built-yet). Needs the demo service (or another real system) first.
- [ ] **Planner agent and workflow** (M). A `plan` ticket is split into proposed feature tickets that a person approves before they are created. First version creates only tickets without dependencies, because the factory does not react to merges yet.
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
| 4 | Planner | A `plan` ticket is split into proposed feature tickets that a person approves | Helps with big goals, but a wrong split costs a lot, so it needs approval | Medium | [ ] |
| 5 | CI-failure triage | When the pipeline fails on a branch, diagnose and propose or open a fix | It reuses the bug workflow, but needs a webhook from Bitbucket Pipelines or Forgejo CI | Medium | [ ] |
| 6 | Docs and `AGENTS.md` upkeep | Onboarding draft for a new repo, and updates when the structure changes | Cheap, and it improves every other workflow's input | Small | [ ] |
| 7 | Release notes | A changelog draft from merged PRs | Useful but low risk and low priority | Small | [ ] |
| 8 | Factory retrospective | A weekly report of repeated problems, from a durable run log | Needs the run log first | Medium | [ ] |

Items 4 and 8 are also on your list above (the planner and the "factory improvement loop"); item 1 is done.

## Other ideas we discussed

- [ ] **Browser image and end-to-end spike** (S). A sandbox image with Chromium and Playwright, so a project can run browser tests as a gate. Spike on the fixture's `web/` folder first: memory use inside the 2 GB sandbox, whether Chromium launches with all capabilities dropped,
  and whether OpenCode can take a screenshot as input. Prerequisite for the scheduled UI check and for UI bugs that can be reproduced with a test.
- [ ] **Token and spending limits** (M). A per-ticket budget first (a project can only lower a factory-wide maximum), then daily and per-repository caps from a host-side usage log, and recognition of the provider's usage-limit errors so a run stops cleanly.
- [ ] **Warn on unknown keys in `.factory/project.json`** (S). Today a misspelled optional key is silently ignored.
- [ ] **Startup check of each role's model** (S). Fail early when a role names a model the login cannot use.
- [ ] **Models and reasoning per role** (S). Try `variant` (low for the roles the controller checks mechanically, high for developer, architect and reviewers) and compare tokens and quality from the run records.
- [ ] **Repo map or language-server tools** (M). A generated repository map (ctags) at sandbox start, or language servers baked into the images, for large repositories.
- [ ] **Marketing visuals** (S). Two non-technical images for slides and marketing: the ticket-to-reviewed-code flow, and a simplified architecture. A first draft of the flow image had card text running to the bottom edge and an overflowing label, and the drafts are gone, so they need to be redone before review.

## Done

- [x] Janitor: removes checkouts of finished runs, lists (and optionally deletes) old work-in-progress branches, and removes old unused repository images.
- [x] Feature workflow: analyst, coverage check, triage, architect, developer, project checks, reviewers, UX review, QA, pull request.
- [x] Bug workflow with the red-then-green proof.
- [x] Incident workflow, advice only, with `/option`, `/hotfix` and `/resolved` and a draft postmortem.
- [x] Quick mode, and notices for comments that cannot be used.
- [x] Pull request revisions: a review that requests changes, or `/fix`, on a factory pull request becomes one new commit on the same branch.
- [x] Run record with model, tokens and cost; work-in-progress branch for runs that stop.
- [x] Egress allowlist with `enforce`, `audit` and `open` modes; per-role models; sandbox images for .NET, Python and Node.
- [x] Documentation, diagrams and the documentation check.
