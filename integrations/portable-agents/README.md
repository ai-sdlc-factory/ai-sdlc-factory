# Portable agents

The 16 agent roles and the 4 flows of dev-foundry, written so they can be used **outside** dev-foundry, in any AI harness that can run a prompt against a repository (Claude Code subagents, OpenCode, GitHub Copilot agent mode, Cursor, a plain script that calls a model API).

Nothing here depends on Forgejo, Temporal, Docker or OpenCode. In dev-foundry those provide the *machinery*; the roles and the flows are plain text.

## What is in here

| Path | What |
| --- | --- |
| `agents/<role>.md` | one file per role: a short front matter (`name`, `description`, `access`, `output`) and the full instructions, ending in the JSON answer the role must give |
| `orchestrator.md` | an optional meta-agent that runs a flow by calling the roles in order, for harnesses that support subagents |
| `flows/<flow>.md` | the four flows as step-by-step procedures: which role, what to give it, what the code must check, what happens on each verdict |
| `flows/flows.json` | the same flows in machine-readable form, for a script or workflow engine |
| `contracts.md` | for every role: the input to give it and the JSON it returns |
| `checks.md` | the checks dev-foundry does in **code**, not in prompts. Reproduce these as scripts or hooks; they are what makes the output trustworthy |
| `standards/` | the short engineering floor and the security checklist, already included in the reviewer and security roles |

## The roles

| Role | Access | Job |
| --- | --- | --- |
| `analyst` | read-only | decides if a feature request is clear enough; lists acceptance criteria |
| `coverage` | read-only | checks which criteria the code already satisfies |
| `architect` | read-only | designs a change that is big or risky |
| `developer` | edit + shell | implements the change and makes the project's checks pass |
| `reviewer` | read-only | reviews the diff for correctness, tests, scope |
| `security` | read-only | reviews the diff for security problems |
| `ux` | read-only | reviews a UI change for usability and accessibility |
| `qa` | read-only | checks that every acceptance criterion is met |
| `bug-analyst` | read-only | turns a bug report into expected vs actual behaviour |
| `reproducer` | edit + shell (tests only) | writes a test that fails because of the bug |
| `diagnostician` | read-only | finds the root cause and says whether the fix is small enough |
| `incident-analyst` | read-only | assesses severity and impact of an incident ticket |
| `investigator` | read + shell (git history only) | names likely causes from code and git history |
| `mitigator` | read-only | proposes mitigation options for people to carry out |
| `postmortem-writer` | edit (one file) | drafts a postmortem from the ticket record |
| `pr-resolver` | edit + shell | addresses review comments on an existing pull request |

`access` is a request, not a guarantee: set it in your harness (tool allow-lists, permissions), do not rely on the prompt. A reviewer that can run commands can also be talked into running them.

## Using one role on its own

1. Copy `agents/<role>.md` into your harness (for example `.claude/agents/`, `.opencode/agents/` or `.github/agents/`) and set the tools that match `access`.
2. Give it the input listed for that role in `contracts.md`: the ticket text, the numbered criteria, the diff. Put untrusted text (tickets, logs, code) inside clearly marked blocks, as the roles expect to treat it as data.
3. Read the **last JSON object** in the answer. Validate it against the shape in `contracts.md` and the limits written in the role. If it fails, send the problem back once ("your answer broke this limit: ...") and ask again.
4. Decide what happens from the `verdict` **in your code**, not by letting the model decide the next step.

## Running a flow

Either follow `flows/<flow>.md` by hand or in a script, or give `orchestrator.md` to an agent that can call subagents. The orchestrator is the weakest link: a model deciding the process is exactly what dev-foundry avoids. If you can, implement the loop in code and use the roles as functions.

## Things to adapt

- **Hard rules in `developer` and `pr-resolver`.** They list protected paths (`.factory/`, `.github/`, CI files). Replace with your own.
- **"The caller commits for you."** In dev-foundry the controller commits and pushes after the checks pass. The roles never commit. Keep it that way, or say so explicitly in your harness.
- **The model line.** Roles carry no model. Dev-foundry uses a stronger model for developer, architect and the reviewers.
- **Limits.** Each role states its answer limits (characters, number of items). They exist so a validator can reject a runaway answer; adjust together with your validator.
