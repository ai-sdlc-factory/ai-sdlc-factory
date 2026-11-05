# Setting up a project

[Documentation index](README.md) | [Getting started](getting-started.md) | [Workflows](workflows.md)

A *project* is a Forgejo repository the factory serves. The factory is already running ([Getting started](getting-started.md)); this guide
connects one repository to it. It takes about five minutes.

## Checklist

| # | Step | Required |
| --- | --- | --- |
| 1 | [Give the bot write access](#1-give-the-bot-access) | **yes** |
| 2 | [Add the webhook](#2-add-the-webhook) | **yes** |
| 3 | [Create the labels](#3-create-the-labels) | **yes** (`feature`, and `bug`/`incident` for those workflows) |
| 4 | [Add `.factory/project.json`](#4-add-factoryprojectjson) on the **default branch** | **yes** |
| 5 | [Add an `AGENTS.md`](#5-add-an-agentsmd) | recommended |
| 6 | [Try it](#6-try-it) | |

## 1. Give the bot access

In the repository: **Settings > Collaborators > add `factory-bot` with Write access** (or make the bot the owner). The bot comments on tickets, pushes
branches (`factory/issue-<n>`, `factory/issue-<n>-wip`) and opens pull requests. It cannot merge and never does.

## 2. Add the webhook

**Settings > Webhooks > Add webhook > Forgejo:**

| Field | Value |
| --- | --- |
| Target URL | `http://api:8000/webhooks/forgejo` (the Compose hostname; Forgejo's allowed host list already contains it) |
| HTTP method | `POST` |
| Content type | `application/json` |
| Secret | the `FORGEJO_WEBHOOK_SECRET` from `infra/.env` (the same for every repository) |
| Trigger on | **Custom events**: **Issues**, **Issue Label**, **Issue Comment** (this also carries `/fix` comments on pull requests). Add **Pull Request Review Rejected** if a review that requests changes should start a [revision](workflows.md#pull-request-revisions) |
| Active | yes |

Use **Test Delivery**. A valid delivery returns `202` and appears in the Temporal UI (http://localhost:8080). Adding a label to an existing ticket
starts a run only if **Issue Label** is enabled; **opening** a ticket with the label already on needs only **Issues**.

## 3. Create the labels

Create these labels in the repository (**Issues > Labels**). Only the first three are needed to start work.

| Label | Effect |
| --- | --- |
| `feature` | starts the [feature workflow](workflows.md#feature-workflow) |
| `bug` | starts the [bug workflow](workflows.md#bug-workflow). Also needed for the incident `/hotfix` command |
| `incident` | starts the [incident workflow](workflows.md#incident-workflow) |
| `quick` | [quick mode](workflows.md#quick-mode) for this ticket |
| `full` | the full process for this ticket, even if the project defaults to quick |
| `needs-architecture` | forces a design review, even in quick mode |

## 4. Add `.factory/project.json`

This is the **only file** the factory needs in a repository. It tells the factory which sandbox image to use and which commands prove a change
is good. It is read from the default branch at the start of each run, so a change applies to the next run. The smallest valid file:

```json
{
  "image": "python",
  "gates": [
    { "name": "test", "command": ["python", "-m", "pytest", "-q"] }
  ]
}
```

Without a usable file, a run stops with the status `needs_config` and a comment that shows an example like this one.

### Reference

| Key | Required | Default | Meaning and limits |
| --- | --- | --- | --- |
| `image` | **yes** | | The sandbox image alias: `dotnet`, `python` or `node` (the aliases your factory offers; the operator can add more in `SANDBOX_IMAGES`). Lower-case letters, digits and `-`, starting with a letter, at most 31 characters |
| `gates` | **yes** | | The project's own checks, **1 to 8**, run in order after every build round. Each is an object, below |
| `gates[].name` | **yes** | | Unique within the file; lower case, letters, digits, `-` and `_`, starting with a letter, at most 30 characters |
| `gates[].command` | **yes** | | The command as an **argument list**, 1 to 32 strings of at most 500 characters. There is no shell: use `["sh", "-c", "..."]` when you need one |
| `gates[].timeout_seconds` | no | `600` | 10 to 1800 |
| `reviewers` | no | `["reviewer"]` | Which review roles run: `reviewer` (code quality), `security`. Each at most once. QA is separate and always runs in the full process |
| `ui` | no | none | Declares a user interface so the `ux` reviewer can run, [below](#ui) |
| `rules` | no | none | Your own mechanical rules, checked on the lines a change adds: up to 20, [below](#rules) |
| `record` | no | `false` | `true` commits a [run record](workflows.md#the-run-record) (prompts, answers, check output, tokens) to `.factory/runs/` on the pull request branch |
| `mode` | no | `"full"` | `"quick"` makes [quick mode](workflows.md#quick-mode) the project's default. Anything other than `"full"` or `"quick"` is refused |

Rules that apply to the whole file:

- It must be a JSON **object** of at most **16 KB**, a regular file (not a symbolic link), in the repository's default branch.
- **Unknown keys are ignored without a warning**, so a typo such as `"gate"` is not reported as an error (the run then stops because `gates` is missing, but a
  misspelled optional key like `"recrod"` silently does nothing). Check the spelling against the table.
- It is **untrusted input**: it can only choose among options the factory already offers. It cannot add models, permissions, images or network hosts.
- Agents **cannot change it**. `.factory/` is a protected path, so only a person can edit it.

### What a good gate is

A gate decides whether a change is acceptable, with its exit code (0 passes). Gates run in a fresh sandbox **without model access**, with package
registries reachable and nothing else.

- **Deterministic and offline**, apart from fetching packages. No secrets, no services to connect to.
- **Self-contained.** A gate starts in a new container each time. The container's `/tmp` is temporary and its root is read-only, but the **workspace is
  writable and persists for the ticket**. A gate that needs dependencies must install them itself, for example into a folder inside the workspace:
  `["sh", "-c", "python -m venv .venv && .venv/bin/pip install -q -r requirements.txt && .venv/bin/python -m pytest -q"]`.
- **Keep generated files out of the change.** Anything a tool writes (`.venv`, `node_modules`, `bin/`, `obj/`, caches) must be covered by `.gitignore`;
  otherwise the change is rejected as build output. The developer is told the same.
- **One concern per gate** (`test`, `lint`, `build`), so a failure names the cause. The end of a failing gate's output is sent back to the developer.
- **Quick enough** for the timeout; slow suites need a larger `timeout_seconds` (at most 1800).

### Examples

**Python** (the image has Python 3.12, pytest and ruff):

```json
{
  "image": "python",
  "gates": [
    { "name": "lint", "command": ["ruff", "check", "."] },
    { "name": "test", "command": ["python", "-m", "pytest", "-q"], "timeout_seconds": 300 }
  ]
}
```

**.NET** (the image has the .NET SDK):

```json
{
  "image": "dotnet",
  "gates": [
    { "name": "build", "command": ["dotnet", "build", "--nologo", "-v", "q"], "timeout_seconds": 600 },
    { "name": "test", "command": ["dotnet", "test", "--nologo", "-v", "q"], "timeout_seconds": 900 }
  ],
  "reviewers": ["reviewer", "security"]
}
```

**Node.js** (Node 22 LTS with npm and corepack; dependencies come through the package registries):

```json
{
  "image": "node",
  "gates": [
    { "name": "install-and-test", "command": ["sh", "-c", "npm ci --no-audit --no-fund && npm test --silent"], "timeout_seconds": 600 }
  ]
}
```

**Everything switched on**, with a web UI:

```json
{
  "image": "node",
  "mode": "full",
  "record": true,
  "reviewers": ["reviewer", "security"],
  "ui": {
    "paths": ["web/**", "src/**/*.tsx", "*.css"],
    "medium": { "files": 3, "lines": 40 },
    "large": { "files": 8, "lines": 200 }
  },
  "gates": [
    { "name": "test", "command": ["sh", "-c", "npm ci --no-audit --no-fund && npm test --silent"] }
  ]
}
```

### Rules

A rule says "a change may not add a line like this in these files". The controller checks it on the diff, so unlike a sentence in `AGENTS.md` it cannot be talked around. Only **added** lines count: old code is not blamed.

```json
"rules": [
  { "id": "no-raw-fetch", "files": "src/**/*.tsx", "forbid": "\\bfetch\\(", "message": "Call the backend through the typed client in src/api.", "severity": "block" },
  { "id": "no-console", "files": ["src/**/*.ts", "src/**/*.tsx"], "forbid": "console\\.log", "message": "Use the logger.", "severity": "warn" }
]
```

| Key | Required inside a rule | Default | Meaning |
| --- | --- | --- | --- |
| `id` | **yes** | | Unique; lower case, letters, digits, `-` and `_`, starting with a letter |
| `files` | **yes** | | A file pattern, or 1 to 5 of them (same syntax as `ui` paths below) |
| `forbid` | **yes** | | A regular expression of at most 300 characters. A line the change adds that matches it breaks the rule. It runs without backtracking, so **lookarounds and backreferences are not supported** |
| `message` | **yes** | | At most 300 characters: what to do instead. It is sent to the developer |
| `severity` | no | `"block"` | `"block"`: the change goes back to the developer until the rule holds (it counts as a failed round). `"warn"`: nothing is blocked; the reviewers are told and decide |

Rules apply to features, bug fixes and [pull request revisions](workflows.md#pull-request-revisions). A rule cannot loosen anything: it only adds a check, and the protected paths and secret scan stay as they are. Use it for what can be tested with a pattern (a forbidden import, a banned call); judgement calls belong in `AGENTS.md`.

### UI

Setting `ui` lets the `ux` reviewer look at interface changes. It runs only when **both** hold: the change touches files that match `paths`, and the change is
**medium or large**. A small UI change, or none, gets no UX review, and the pull request says which. The decision is made by counting, not by an agent.

| Key | Required inside `ui` | Default | Meaning |
| --- | --- | --- | --- |
| `paths` | **yes** | | 1 to 20 patterns for the UI files |
| `medium` | no | `{ "files": 3, "lines": 40 }` | medium or larger from this many UI files **or** this many changed lines in them (`files` 1 to 10000, `lines` 1 to 1000000) |
| `large` | no | `{ "files": 8, "lines": 200 }` | large from this many (must not be smaller than `medium`) |

Patterns: `*` matches within one folder, `**` across folders, `?` one character. A pattern without a slash is matched against the file name wherever it is, so
`*.css` means every stylesheet. Allowed characters are letters, digits and `. _ - @ + / * ?`; no leading `/` and no `..`.

## 5. Add an `AGENTS.md`

A short `AGENTS.md` in the repository root is read by OpenCode, so the agents know your conventions. It helps most for the **developer** (and the architect): in a
test, a developer that was told "tests go in `check_x.py`, plain asserts, no pytest" wrote exactly that, though the ticket never said so.

Suggested outline (short headings, a few lines each; the agents read the whole file on every run, so every line costs tokens):

```markdown
# <Project name>
## What this is          one or two sentences: the product and who uses it
## Layout                the folders that matter and what lives in each
## Build and test        the exact commands, and which of them the gates run
## Conventions           naming, error handling, logging, where new code goes
## Architecture rules    layering ("Domain must not reference Infrastructure"), allowed dependencies
## Testing               framework, where tests live, how a good test looks here
## UI and UX             design system, accessibility expectations (only for projects with a UI)
## Do not touch          generated code, vendored folders, files a person owns
```

Rules that can be tested with a pattern belong in [`rules`](#rules) instead, where they are enforced. The factory also applies a short, project-independent [engineering floor](../agents/README.md#the-engineering-floor) in its review roles; `AGENTS.md` may add to it, never relax it.

Good content: what the project is, the folder layout, how to build and test, naming and style rules, and what **not** to touch. Keep it short, and keep it
consistent with `gates`: if `AGENTS.md` says tests run with one command and `gates` runs another, the gate wins. The analyst, coverage and architect roles
treat repository text as data and will not obey commands in it, so write conventions, not instructions aimed at them. You can ask the factory to draft one with a
`feature` ticket, then edit it: it can read the code, but only you know the rules that are not visible in it.

## 6. Try it

1. Create a ticket with a clear, small request, for example: *"Add a function `farewell(name)` to `hello.py` that returns `Goodbye, <name>!`, with a test."*
2. Add the `feature` label (or add it when creating the ticket).
3. Within a few seconds the bot comments "The factory picked up this feature ticket". The analyst, coverage check and the rest follow, each as a comment.
4. When it is done there is a pull request `factory/issue-<n>`. Review it like any other, and merge it yourself.

To run a ticket again, **close and reopen** it. To follow a run in detail, open it in the Temporal UI; the [operations guide](operations.md) lists what to look at.

## What the factory will not do

- It never merges, and never pushes to your default branch.
- It never changes `.factory/`, `.forgejo/`, `.github/`, `.gitea/`, `.git/`, or CI pipeline files (`bitbucket-pipelines.yml`, `.gitlab-ci.yml`, `Jenkinsfile`). A ticket that
  needs one of these changed ends with `build_blocked`; make that change yourself.
- It never creates symbolic links, and never publishes a file that looks like it holds a credential.
- It cannot reach anything but the model provider and package registries, and never your production systems.

## Troubleshooting a new project

| Symptom | Fix |
| --- | --- |
| no comment appears on the ticket | the webhook is missing or lacks Issues/Issue Label events; use **Test Delivery**; check the bot has write access |
| "`.factory/project.json` cannot be used: ..." | the comment names the problem; fix it on the default branch, then close and reopen the ticket |
| a gate fails with "command not found" | the tool is not in the sandbox image: install it in the gate, or add it to the image ([launcher guide](../services/sandbox-launcher/README.md#images)) |
| the change is rejected as "build output" | add the generated folder to `.gitignore` |
| the run stops with `build_blocked` | the ticket needs a protected file changed ([above](#what-the-factory-will-not-do)) |
| a pull request has a surprising extra file | review it, then merge or ask for removal; the PR lists each file |
