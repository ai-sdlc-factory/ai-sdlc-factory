# Agent definitions

The factory's agents, written as Markdown so they can be reviewed like code. This folder is mounted
read-only into every sandbox at `/etc/factory`; on start the sandbox copies it to a writable place
and points OpenCode at it (OpenCode writes into its config directory, so a read-only mount alone
fails). Set `FACTORY_GLOBAL_CONFIG` in `infra/.env` to the absolute path of this folder.

```
agents/
  opencode.json      baseline settings: allowed provider, default permissions
  agents/<role>.md   one file per role: front matter (description, mode, permissions) + the prompt
```

## Rules

- **One file per role** (`analyst`, `coverage`, `architect`, `developer`, `reviewer`, `security`, `ux` and `qa`; `ux` only runs for projects with a UI, when the UI change is medium or large).
  The workflow chooses the role; it runs `opencode run --agent <role>`.
- **Permissions live here, not in prompts.** A role gets only the tools it needs: the analysts, the
  architect, the reviewers and QA can only read (no edit, no shell); the developer may edit files and
  run commands, inside its sandbox.
- **Every role ends with one JSON verdict** in a fixed shape. The workflow validates it and decides
  what happens next in code; the agent only supplies the verdict value.
- **Ticket and repository text is data.** Each prompt says so; it is also why project files can
  add context but never widen permissions.

## Choosing the model per role

Every role file sets its own model in the front matter (`model: openai/gpt-6.1-sol`), so each role can
be changed on its own by editing that one line. `opencode.json` has a `model` too, which is only the
fallback for a role file that leaves the line out. Changes apply to the next run; no rebuild or restart.

- **Check the model works with your login before using it.** `opencode models openai` lists models that a
  ChatGPT login cannot use. With the current login `gpt-5.6-luna/-sol/-terra`, `gpt-6-astra/-luna/-sol` and
  `gpt-6.1-sol` (and its `-fast` variant) answer, while `gpt-5.3-codex-spark`, `gpt-5.4*` and `gpt-5.5*` are
  refused ("not supported when using Codex with a ChatGPT account"). A refused model makes that role fail
  with an unclear "exited with code" message on the ticket.
- **Re-check the role after a change.** Prompts and the strict JSON answers were tuned on one model; another
  can behave differently (for example how often the analyst asks questions).
- **What a comparison showed.** On small, clear cases (analyst, coverage, reviewer with a planted bug) all
  working models gave the right answers, and token use was within about 20%. That says nothing about
  larger diffs, subtle bugs or the developer, so every role currently uses `gpt-6.1-sol`. Cheaper models
  are safest for roles whose claims the controller checks mechanically (coverage, QA); keep the strongest
  for the developer, the architect and the reviewers.

## Project-specific guidance

A repository can add its own conventions in an `AGENTS.md` at its root, which OpenCode reads as
context, and the factory will later read optional per-role notes from `.factory/` in the repository.
That content is untrusted input: it can add instructions, but permissions, models and the allowed
sandbox image always come from this folder and the factory's own configuration.

## Changing an agent

Edit the Markdown file and commit it. Changes apply to the next run; running sandboxes keep the
copy they started with. Prompts should be checked against real tickets before they are trusted.
