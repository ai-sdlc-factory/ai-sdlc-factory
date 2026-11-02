# Feature factory design

Status: step 1 of the build order is done (router, Forgejo client and bot account, a skeleton
`FeatureWorkflow`). Everything else below is still design.

This document describes how a Forgejo ticket turns into a
reviewed pull request, using role agents that run in disposable OpenCode containers. It follows
the stage-and-artifact idea of Anthropic's AI-native SDLC playbook: every stage produces a
reviewable artifact, deterministic code controls the flow, and humans own the gates.

## Flow

```
Forgejo issue (label: feature)
  → webhook → API (verify, start ForgejoEventWorkflow)
  → router decides: new feature → start FeatureWorkflow  |  comment → signal a waiting workflow
```

`FeatureWorkflow` stages:

1. **Intake** (deterministic): check the label, ignore duplicates, create the workspace (a git
   clone on the host) and a working branch.
2. **Analyst** (agent, read-only): validates the requirements, lists acceptance criteria, reports
   complexity facts. If something is unclear it asks on the ticket (see Human communication).
3. **Coverage check** (agent, read-only): is this already implemented, or partly covered? Fully
   covered → comment on the ticket and stop. Partly → narrow the scope.
4. **Triage** (deterministic): fixed rules over the analyst's facts decide whether the architect is
   needed. The label `needs-architecture` forces it.
5. **Architect** (agent, read-only, optional): design notes, affected components, risks.
6. **Developer** (agent, read-write on the workspace): implements on the working branch.
7. **Deterministic gates**: build, tests, format, lint, secret scan, dependency scan. Plain commands
   with exit codes, defined per project. Agents never decide whether these passed.
8. **Reviewers** (agents, read-only): code quality, and optionally security.
9. **QA** (agent): checks every acceptance criterion and writes and runs tests.
10. **Publish** (controller): pushes the branch, opens the PR with a summary and the findings.
    A human code owner reviews and merges. The factory never merges.

Steps 6 to 9 form a loop: any `needs_changes` goes back to the developer, with a cap on rounds.

## Deterministic control

- Every agent run ends with a JSON verdict, validated against a schema:
  `verdict` (`pass`, `needs_changes`, `blocked`, `reject`), `findings` (severity, text, location),
  and `artifacts` (paths of files the stage produced).
- Invalid or missing verdict: one retry, then escalate to a human.
- Routing is a transition table in workflow code. Agents supply only the enum value.
- Loop cap (default 3 rounds per loop), then comment on the ticket and wait for a human.
- Each stage's artifacts are committed or attached so the history is auditable.

## Human communication

All communication is on the ticket. When a workflow needs a human it posts a comment and waits for
a Temporal signal; nothing runs while it waits. A reply in the ticket arrives as an `issue_comment`
webhook, the router signals the workflow, and the workflow continues. Comments written by the
factory's own Forgejo account are ignored so it cannot answer itself. Add a reminder or timeout so
stalled tickets stay visible.

## Components and trust

- **api**: verifies webhooks, starts the router workflow. No business logic.
- **controller**: workflows, routing, verdict handling, and the only holder of the Forgejo token
  (reads tickets, comments, pushes branches, opens PRs).
- **sandbox launcher**: a separate activity worker on its own task queue. It is the only service
  with Docker access, because starting containers from code requires it. Treat it as highly
  privileged: its policy is fixed in code and agents cannot change it (see Sandbox policy).
- **disposable agent containers**: one per agent run, started by the launcher, removed afterwards.

## Sandbox policy (enforced by the launcher, not requested by projects)

- Images come from an allowlist kept by the factory (for example `dotnet`, `python`, `node`). A
  project picks one by name; it cannot name an arbitrary image.
- Mounts are only the run's workspace (read-only for analyst, coverage, architect and reviewers;
  read-write for the developer and QA) and the global OpenCode settings, read-only.
- No Docker socket, no host mounts, no Forgejo or Temporal credentials inside the container.
- Resource limits, a run timeout, read-only root filesystem with a tmpfs for scratch, dropped
  capabilities and `no-new-privileges`, as for the current OpenCode worker.
- The model is chosen per role by the factory's configuration.

## Project configuration

Each project describes itself in a file in its repository, for example `.factory/project.yml`:
image name from the allowlist, the build, test, lint and scan commands, and optional per-role
hints. It is untrusted input: it selects among options the factory already allows and never widens
permissions. Global OpenCode settings are mounted alongside the project's own OpenCode settings.
Where they conflict, the global permission rules must win; verify how OpenCode merges its
configuration before relying on this.

## Other workflows (later)

- **Bug**: reproduce with a failing test first, find the root cause, fix, add a regression test.
  The architect is skipped by default. Same activities, different flow.
- **Incident**: read-only diagnosis; any action beyond that is a proposed runbook step that a
  human authorizes.

## Build order

1. Router in the controller: label handling, comment signals, a bot account, Forgejo client.
2. Workspace creation on the host and the sandbox launcher with one allowlisted image.
3. Analyst stage with verdict schema, ticket comment, and a wait for a human reply.
4. Coverage check and triage rules.
5. Developer plus deterministic gates, then reviewers and QA with the capped loop.
6. Publish and PR creation.
7. Architect stage, bug workflow, then incident handling.

## Open points

- The workspace lives on the host; the launcher runs in a container, so the workspace root must be
  mounted into it at the same path that Docker uses on the host.
- Sharing the ChatGPT login with disposable containers: a read-only mount may stop token refresh.
  Check how OpenCode refreshes and decide where the token lives.
- Which models per role, and how much usage each stage may consume.
