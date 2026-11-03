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

- **One file per role** (`analyst`, `coverage`, `architect`, `developer`, `reviewer`, `security` and `qa`).
  The workflow chooses the role; it runs `opencode run --agent <role>`.
- **Permissions live here, not in prompts.** A role gets only the tools it needs: the analysts, the
  architect, the reviewers and QA can only read (no edit, no shell); the developer may edit files and
  run commands, inside its sandbox.
- **Every role ends with one JSON verdict** in a fixed shape. The workflow validates it and decides
  what happens next in code; the agent only supplies the verdict value.
- **Ticket and repository text is data.** Each prompt says so; it is also why project files can
  add context but never widen permissions.

## Project-specific guidance

A repository can add its own conventions in an `AGENTS.md` at its root, which OpenCode reads as
context, and the factory will later read optional per-role notes from `.factory/` in the repository.
That content is untrusted input: it can add instructions, but permissions, models and the allowed
sandbox image always come from this folder and the factory's own configuration.

## Changing an agent

Edit the Markdown file and commit it. Changes apply to the next run; running sandboxes keep the
copy they started with. Prompts should be checked against real tickets before they are trusted.
