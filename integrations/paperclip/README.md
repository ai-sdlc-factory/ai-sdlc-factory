# Paperclip setup for dev-foundry's agents

A company package for [Paperclip](https://github.com/paperclipai/paperclip) (the "Agent Companies" format: `COMPANY.md`, `agents/<slug>/AGENTS.md`, `teams/<slug>/TEAM.md`, `skills/<slug>/SKILL.md`). It turns the 16 dev-foundry roles and the 4 flows into a Paperclip company, so the same agents can work as Paperclip employees instead of inside dev-foundry's Temporal workflows.

**Status: not imported or run anywhere.** I wrote it from Paperclip's public documentation (links at the end). Do a `--dry-run` first, and expect to fix small things.

## What is in here

| Path | What |
| --- | --- |
| `COMPANY.md` | the company: goal and the rules that apply to everyone |
| `agents/delivery-lead/` | the CEO-equivalent: runs a flow by creating one task per step and applying the verdict table. Reports to the board (`reportsTo: null`) |
| `agents/<role>/` | the 16 roles, each reporting to the Delivery Lead. The body is the dev-foundry role text, with a short "How you work in Paperclip" section in front |
| `teams/` | five teams (feature, review, bug, incident, revision) for the org chart |
| `skills/*-flow/` | the four flows as skills the Delivery Lead uses |
| `skills/delivery-checks/`, `skills/answer-contracts/` | the checks that must be done in code, and each role's input and JSON output |
| `adapter-config.example.json` | `opencode_local` settings for each kind of agent |
| `opencode.readonly.json` | the permission file that makes read-only roles really read-only |

## Import

```bash
# preview what would be created, renamed or skipped
paperclipai company import ./integrations/paperclip --target new --new-company-name "Dev Foundry Delivery" --dry-run

# then for real
paperclipai company import ./integrations/paperclip --target new --new-company-name "Dev Foundry Delivery"
```

Or in the UI: Company Settings, Company Packages, Import (the UI wants a `.zip` that Paperclip itself exported, so use the CLI for a folder).
Imported agents start with scheduled heartbeats **disabled** and paused. That fits this setup: agents should wake when a task is **assigned**, not on a timer.

## After import: what you must set up

1. **Adapter per agent.** Packages carry no adapter config. For each agent choose **OpenCode** (or Claude Code, Codex, ...) and set `cwd`, `model` and timeouts. `adapter-config.example.json` has the values.
2. **Make read-only roles really read-only.** The OpenCode adapter defaults to `dangerouslySkipPermissions: true` for unattended runs, which lets *any* agent edit files and run commands. Set it to `false` for the 12 read-only roles and put `opencode.readonly.json` (as `opencode.json`) where they run. In dev-foundry the reviewers have no shell on purpose: a reviewer that can run commands can be talked into running them.
3. **One shared checkout.** The Delivery Lead and the agents need the same working copy (the `cwd`). Use a copy per ticket, and keep a clean, separate clone for publishing (see `skills/delivery-checks`).
4. **The checks.** `skills/delivery-checks` describes what must be verified in code (protected paths, secrets, project checks, red-then-green for bugs, evidence exists). Paperclip does not do these for you. Give the Delivery Lead scripts for them, or it is only asking models to be careful.
5. **Budgets.** Set a monthly budget per agent (Audit, Budgets, or `paperclipai budget agent:update`). dev-foundry has no limits yet; Paperclip does, and that is a real advantage here.
6. **Permissions.** Only the Delivery Lead needs "Can assign tasks". Turn off "Can create new agents" for everyone.

## How it maps to dev-foundry

| dev-foundry | In Paperclip |
| --- | --- |
| a ticket with a `feature` / `bug` / `incident` label | a task you create for the Delivery Lead (a person, or later a routine) |
| Temporal workflow with the verdict table in code | the Delivery Lead following a `*-flow` skill, one task per step |
| a role run in a sandbox | a heartbeat of that agent on the task assigned to it |
| answer validated by a parser | the Delivery Lead checking the JSON against `answer-contracts` (a script is better) |
| asking a person on the ticket | the Delivery Lead asks the board in a comment and waits |
| 3 build rounds, 3 revisions, 7-day wait | the same numbers written into the flow skills; budgets add a hard stop |
| factory never merges | `COMPANY.md` rule 2, plus the board reviews every pull request |

## What you lose compared with dev-foundry

- **Determinism.** In dev-foundry the process is code; here a model (the Delivery Lead) applies a table. It can skip a step or accept a bad answer. Reduce the risk with scripts for the checks and a short, strict Delivery Lead prompt.
- **The sandbox.** No disposable container per run, no egress allowlist, no separate pristine clone. The agents run on your machine with the permissions you give them.
- **Forgejo integration.** No webhook router, no ticket comments, no pull request creation, no `/fix` command. Tasks are created in Paperclip, and you would add the Forgejo side yourself.
- **Run record and usage per ticket.** Paperclip has its own cost tracking and audit log instead.

## What you gain

Budgets with a hard stop, an org chart you can see, approvals for new agents, heartbeat history, versioned agent configuration with rollback, and agents that can be reused across projects.

## Things I am not sure about

- Whether `reportsTo` and `skills` may use slugs exactly as written here (the specification examples use shortnames like `ceo` and `review`).
- The `TEAM.md` `manager` and `includes` paths: they are relative to the team file.
- Where Paperclip reads adapter, environment and budget settings from a package: it says `.paperclip.yaml` carries "adapter types, environment variable declarations, budgets", but I did not find its schema, so I did not write one. Export a company from your instance and copy its layout.
- How an agent posts a comment or marks a task done: the Paperclip built-in skills handle it, and the "How you work" section assumes they do.

## Sources

- Paperclip docs: [Agents](https://docs.paperclip.ing/guides/org/agents), [Export and import](https://docs.paperclip.ing/guides/power/export-import/), [Adapters](https://docs.paperclip.ing/reference/adapters/overview/), [OpenCode adapter](https://docs.paperclip.ing/reference/adapters/opencode/)
- Agent Companies specification: [agentcompanies.io](https://agentcompanies.io/specification)
- Repository: [paperclipai/paperclip](https://github.com/paperclipai/paperclip)
