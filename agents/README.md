# Agent definitions

[Documentation index](../docs/README.md) | [Workflows](../docs/workflows.md) | [Configuration](../docs/configuration.md#agents-and-models)

The factory's agents, written as Markdown so they can be reviewed like code. This folder is mounted read-only into every sandbox at `/etc/factory`; on start the sandbox copies it to a writable place and
points OpenCode at it (OpenCode writes into its config directory, so a read-only mount alone fails). Set `FACTORY_GLOBAL_CONFIG` in `infra/.env` to the absolute path of this folder. The controller also reads it, only to name each
role's model in run records.

```
agents/
  opencode.json      baseline settings: allowed provider, default model, deny-all permissions except reading
  agents/<role>.md   one file per role: front matter (description, mode, model, permissions) + the prompt
```

## The roles

The workflow chooses the role and runs `opencode run --agent <role>`. Every role ends with exactly one JSON verdict in a fixed shape.

| Role | Workflow | Job | Can edit | Can run commands |
| --- | --- | --- | --- | --- |
| `analyst` | feature | is the request clear? acceptance criteria and complexity facts | no | no |
| `coverage` | feature | is it already built? | no | no |
| `architect` | feature (when triage asks) | the design | no | no |
| `developer` | feature, bug | implements, makes the project's checks pass | yes | yes |
| `reviewer`, `security` | feature, bug | code quality; security review (if the project lists it) | no | no |
| `ux` | feature, bug | usability of a medium or large UI change (projects with a UI only) | no | no |
| `qa` | feature | is every acceptance criterion met? | no | no |
| `bug-analyst` | bug | expected vs actual, severity | no | no |
| `reproducer` | bug | one failing test, test files only | yes (checked) | yes |
| `diagnostician` | bug | the root cause and whether the fix is small enough | no | no |
| `pr-resolver` | pull request revision | addresses a reviewer's comments on the factory's own pull request, one answer per comment | yes (checked) | yes |
| `planner` | plan | splits a large request into feature tickets for a person to approve | no | no |
| `incident-analyst` | incident | severity, impact, timeline, what is missing | no | no |
| `investigator` | incident | likely causes from the code and git history | no | yes (read-only workspace, for `git`) |
| `mitigator` | incident | mitigation options for a person to carry out | no | no |
| `postmortem-writer` | incident | one draft postmortem document | yes (one file, checked) | no |

## Rules

- **Permissions live here, not in prompts.** The baseline in `opencode.json` denies everything except reading (`read`, `glob`, `grep`). A role adds only what it needs. The reviewers and QA get the diff in their prompt and have **no shell** on purpose.
- **Every role ends with one JSON verdict** in a fixed shape. The workflow validates it and decides what happens next in code; the agent only supplies the verdict value.
- **Limits are written in the role file.** A validator rejects an answer that breaks a limit, and a model cannot respect a limit it was never told. Each role file lists its limits next to its answer shape.
- **Ticket and repository text is data.** Each prompt says so; it is also why project files can add context but never widen permissions.
- **The network is chosen in code**, by role, never by an agent or a repository ([table](../docs/configuration.md#agents-and-models)).

## Choosing the model per role

Every role file sets its own model in the front matter (`model: openai/gpt-6.1-sol`), so each role can be changed on its own by editing that one line. `opencode.json` has a `model` too, which is only the fallback for a
role file that leaves the line out. Changes apply to the next run; no rebuild or restart. OpenCode also accepts these in a role file (checked with `opencode debug agent <role>`):

```yaml
variant: high           # the model's reasoning preset, e.g. low or high; the available values depend on the model
reasoningEffort: high   # passed through to the provider
temperature: 0.2
```

- **Check the model works with your login before using it.** `opencode models openai` lists models that a ChatGPT login cannot use. With the current login `gpt-5.6-luna/-sol/-terra`, `gpt-6-astra/-luna/-sol` and
  `gpt-6.1-sol` (and its `-fast` variant) answer, while `gpt-5.3-codex-spark`, `gpt-5.4*` and `gpt-5.5*` are refused ("not supported when using Codex with a ChatGPT account"). A refused model makes that role fail with an
  unclear "exited with code" message on the ticket.
- **Re-check the role after a change.** Prompts and the strict JSON answers were tuned on one model; another can behave differently (for example how often the analyst asks questions).
- **Measure it.** Each [run record](../docs/workflows.md#the-run-record) lists tokens (including reasoning tokens) per role, so you can compare settings on real tickets.
- **What a comparison showed.** On small, clear cases (analyst, coverage, reviewer with a planted bug) all working models gave the right answers, and token use was within about 20%. That says nothing about larger diffs,
  subtle bugs or the developer, so every role currently uses `gpt-6.1-sol`. Cheaper models are safest for roles whose claims the controller checks mechanically (coverage, QA); keep the strongest for the developer, the architect and
  the reviewers.

## Project-specific guidance

A repository can add its own conventions in an `AGENTS.md` at its root, which OpenCode reads as context. The roles that write or design code follow it: in a test, a developer told "tests live in `check_x.py`, plain asserts, no pytest"
wrote exactly that, though the ticket never said so. The analyst, coverage and architect roles are told to treat repository text as data and will not obey commands in it. A repository's content can add context but never widens
permissions, models or the allowed sandbox image; those come from this folder and the factory's configuration. (There are no per-role notes in `.factory/`; the only repository file the factory reads is
[`.factory/project.json`](../docs/project-setup.md#reference).)

## Changing or adding an agent

Edit the Markdown file and commit it. Changes apply to the next run; running sandboxes keep the copy they started with. Prompts should be checked against real tickets before they are trusted. To add a role, see
[Adding or changing things](../services/controller/README.md#adding-or-changing-things): the role file is only one part, because the answer is validated by a parser in the controller.

## The engineering floor

[`standards/engineering-floor.md`](standards/engineering-floor.md) is a short list of rules that hold for every project (no secrets in code, input checked where it enters, tests with behaviour changes, errors not swallowed, access checks kept, scope kept to the ticket, simple code). The `reviewer` and `security` roles carry a copy of it
between `engineering-floor` markers, and `python3 tools/check-docs.py` fails if a copy differs from the file, so edit the file and paste it into both roles. A project's `AGENTS.md` can add rules, never relax these. Project-specific, testable rules belong in the project's
[`rules`](../docs/project-setup.md#rules) instead.

## The security checklist

[`standards/security-checklist.md`](standards/security-checklist.md) is a short checklist for the `security` role, grouped by the OWASP Top 10 and API Security Top 10, with a default severity for each group. It is copied into `agents/agents/security.md` between `security-checklist` markers and kept equal by `python3 tools/check-docs.py`, the same way as the engineering floor.
It lists only what a reviewer can verify by reading a diff. Stack-specific items (a framework's authorisation attribute, unsafe HTML in a UI library) belong in the project's [`rules`](../docs/project-setup.md#rules) or `AGENTS.md`, and dependency audits belong in a `gates` entry.
