# Feature factory design

Status: steps 1 to 4 of the build order are done, plus the architect (step 7) and the developer with its
automated checks, the reviewers and QA, and the pull request (steps 5 and 6): a labelled ticket becomes
a reviewed pull request. The bug workflow is built too; incidents below are still design.

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
9. **UX review** (agent, read-only, conditional): for a project that declares a UI, only when the change
   touches UI files and the UI change is medium or large by file and line counts (small changes are not
   worth it). The decision is made by code, and the pull request says why it was skipped.
10. **QA** (agent): checks every acceptance criterion and writes and runs tests.
11. **Publish** (controller): pushes the branch, opens the PR with a summary and the findings.
    A human code owner reviews and merges. The factory never merges.

Steps 6 to 10 form a loop: any `needs_changes` goes back to the developer, with a cap on rounds.

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
- A network profile per run (none by default), enforced by internal networks and an egress proxy rather
  than by the agent's good behaviour.
- Resource limits, a run timeout, read-only root filesystem with a tmpfs for scratch, dropped
  capabilities and `no-new-privileges`, as for every other container in the stack.
- The model is chosen per role by the factory's configuration.

## Project configuration

Each project describes itself in a file in its repository, for example `.factory/project.yml`:
image name from the allowlist, the build, test, lint and scan commands, and optional per-role
hints. It is untrusted input: it selects among options the factory already allows and never widens
permissions. Global OpenCode settings are mounted alongside the project's own OpenCode settings.
Where they conflict, the global permission rules must win; verify how OpenCode merges its
configuration before relying on this.

## Other workflows (later)

- **Bug** (built): a `bug` ticket starts a `BugWorkflow` on the same shared stages. A bug analyst reads the report, a
  reproducer writes a failing test, and the *factory* (not the agent) checks that the test fails on the unchanged
  code; a diagnostician finds the root cause; the developer fixes it; the factory checks that the same test now passes
  and was not edited, along with the project's checks; reviewers judge the change. QA and the architect are not used:
  the failing-then-passing test is the proof. See the [controller guide](../services/controller/README.md#bug-tickets).
- **Incident**: read-only diagnosis; any action beyond that is a proposed runbook step that a
  human authorizes.

## Build order

1. Router in the controller: label handling, comment signals, a bot account, Forgejo client.
2. Workspace creation on the host and the sandbox launcher with one allowlisted image.
3. Analyst stage with verdict schema, ticket comment, and a wait for a human reply (done).
4. Coverage check and triage rules (done).
5. Developer plus deterministic gates (done), then reviewers and QA with the capped loop.
6. Publish and PR creation (done, currently straight after the gates).
7. Architect stage (done), bug workflow (done), then incident handling. For incidents, a separate collector that
   watches production and files tickets (so no agent gets production access) is the intended design, but it waits
   until there is a production system to watch.

## Open points

- The workspace lives on the host; the launcher runs in a container, so the workspace root must be
  mounted into it at the same path that Docker uses on the host.
- Sharing the ChatGPT login with disposable containers: the `opencode-data` volume is mounted into
  runs that need the model (see the launcher guide). Still to confirm with a real login: that a
  refreshed token is written back to the volume. Earlier note: a read-only mount may stop token refresh.
  Check how OpenCode refreshes and decide where the token lives.
- Which models per role, and how much usage each stage may consume.

## Findings from running it

- The analyst tends to set `public_api_change` for any new function, even in a small single-file
  program, which makes triage ask for a design review. If that proves noisy, tighten the role prompt
  (a public API is one other projects or users depend on) before changing the triage rules.
- The coverage agent is strict on purpose: a test that calls a function directly does not count as
  "a test that runs the program". Expect more `partial` than `covered`.
- Agent answers must be validated against limits the agent was told about. A first architect run
  failed twice because the validator allowed 300 characters per change, 20 components and no trailing
  slash on directories, none of which the role file mentioned. Limits are now stated in each role
  file and rejections name the exact field and limit.
- Tickets are started again by closing and reopening them; re-labelling does nothing for a ticket that
  already had a run.
- The architect's `needs_decision` and `reject` paths, the build loops and the publish failures cannot be
  made to happen on demand with a real model (a person who answers the analyst settles the fork first,
  and a contradictory request is rejected by the analyst). They are covered by workflow scenario tests
  with scripted agents on a Temporal dev server; breaking the round cap or the reopen-restarts rule on
  purpose makes exactly the matching scenario fail.

## Quick mode

A `quick` label (or `"mode": "quick"` in the project file; a `full` label overrides both) skips the coverage
check, the design review and QA for a small change, and keeps the analyst, developer, project checks, reviewers and
the human review of the pull request. The choice belongs to a person; see the
[controller guide](../services/controller/README.md#quick-mode).

## Run record

With `"record": true` in `.factory/project.json`, the controller commits what each agent was told and answered,
and each check's output, to `.factory/runs/issue-<n>-<run id>/` on the pull request or work-in-progress branch (see the
[controller guide](../services/controller/README.md#run-record-record-true)). It exists because Temporal keeps
history for 7 days, and a reviewer should be able to see how a change came about. The controller writes it, not the
agents, and credential-like lines are removed first. Old records are read by later agents only if the repository's
own files are (they are ordinary files), so a project that does not want that can add `.factory/runs/` to its
agents' ignore rules.

## Known risks of the build stage

- **Only the developer has a shell.** The reviewers and QA are read-only and are given the diff by the
  controller, so the agents that judge the work cannot run commands.
- **The developer can run commands, and the OpenCode login is readable inside its sandbox.** An agent
  that is tricked by text in the ticket or repository could try to send the login or the code out. The
  network is therefore an allowlist ([`egress/`](../egress/README.md)): sandboxes have no route out and
  no external DNS, and an egress proxy tunnels HTTPS only to the hosts of the run's profile (the developer:
  the model provider and package registries; the read-only agents: the model provider only; every
  decision is logged). That stops sending anything to an arbitrary server and reaching your own network.
  The restriction can be relaxed on purpose (`SANDBOX_EGRESS=audit` allows and logs everything over HTTPS,
  `open` removes it), or replaced by your own rules file. It cannot stop data going to a host that is allowed: the model provider receives whatever is in a
  prompt, and the proxy sees host names, not content.
- **Gate commands come from the repository.** They run inside the sandbox, like the agent, so they get no
  more reach than the agent has, but a repository owner decides what runs.
- **The secret scan is a safety net**, not a guarantee: it finds common token shapes and the factory's own
  token.
- **Findings from running it:** the first run produced a good pull request in one round; the pull request
  body initially quoted the maximum round count instead of the passing round (fixed). A request to change
  `.factory/` was refused by the developer itself and reported as blocked.
- A first live run with both reviewers and QA took about a minute from ticket to pull request and passed in
  one round. Everything that sends work back (blocking findings, unmet or unverifiable QA criteria, the
  three-round cap, unusable reviews) is covered by scenario tests with scripted agents; breaking the
  feedback to the developer or QA\'s evidence check on purpose makes exactly the matching tests fail.
- A run that cannot finish used to lose its work: the checkout is deleted when the workflow ends and the only
  publish step is the last one. Now the controller saves what was written to a `factory/issue-<n>-wip` branch
  and says so on the ticket, with a link to the full record in Temporal (kept 7 days by default). A first live
  test showed both outcomes: a developer that implemented the feature and then stopped because a project check
  could never pass left its two files on the branch, and a ticket that needed a protected file changed left
  nothing and said so. Testing this also exposed a retry bug in publishing: after an attempt that had
  committed, the next one found "no changes". The pristine clone is now reset before every inspection.
