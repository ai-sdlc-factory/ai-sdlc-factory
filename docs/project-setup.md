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
| `image` | **yes** | | The sandbox image alias: [`dotnet`, `python`, `node`, `dotnet-node`, `python-node` or `full`](#choosing-an-image) (the aliases your factory offers; the operator can add more in `SANDBOX_IMAGES`). Lower-case letters, digits and `-`, starting with a letter, at most 31 characters |
| `gates` | **yes** | | The project's own checks, **1 to 8**, run in order after every build round. Each is an object, below |
| `gates[].name` | **yes** | | Unique within the file; lower case, letters, digits, `-` and `_`, starting with a letter, at most 30 characters |
| `gates[].command` | **yes** | | The command as an **argument list**, 1 to 32 strings of at most 500 characters. There is no shell: use `["sh", "-c", "..."]` when you need one |
| `gates[].timeout_seconds` | no | `600` | 10 to 1800 |
| `gates[].cwd` | no | the repository root | A folder inside the repository to run the command in, such as `frontend`. Letters, digits and `. _ - @ + /`, no leading `/` and no `..`. A folder that does not exist fails the gate |
| `reviewers` | no | `["reviewer"]` | Which review roles run: `reviewer` (code quality), `security`. Each at most once. The acceptance check is separate and always runs in the full process |
| `ui` | no | none | Declares a user interface so the `ux` reviewer can run, [below](#ui) |
| `rules` | no | none | Your own mechanical rules, checked on the lines a change adds: up to 20, [below](#rules) |
| `limits` | no | the factory's defaults | How many tries and questions a run may use, [below](#limits) |
| `protected_paths` | no | none | Extra files the agents may never change, [below](#protected-paths) |
| `record` | no | `false` | `true` commits a [run record](workflows.md#the-run-record) (prompts, answers, check output, tokens) to `.factory/runs/` on the pull request branch |
| `verify` | no | none | How to start the application and run end-to-end tests for a [`/qa` run](workflows.md#qa-run), [below](#end-to-end-checks-verify) |
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
- **A check must not write files that git would show.** Checks run after the developer has finished, so a file a check creates (or recreates) lands in the change, the reviewers flag it as unrelated, and the developer cannot remove it because the next round's check puts it back. The usual causes:
  `npm install` without a committed lockfile (it writes `package-lock.json`: commit the lockfile and use **`npm ci`**, which writes nothing), a formatter that rewrites files (use its check mode), and tools that leave files like `*.tsbuildinfo` (add them to `.gitignore`).
  Test it once: run your checks on a clean clone and look at `git status`. The [templates](../templates/README.md) all pass that test. Do **not** put the lockfile alone in `protected_paths`: see [Protected paths](#protected-paths).
- **One concern per gate** (`test`, `lint`, `build`), so a failure names the cause. The end of a failing gate's output is sent back to the developer.
- **Quick enough** for the timeout; slow suites need a larger `timeout_seconds` (at most 1800).

### End-to-end checks (`verify`)

The `verify` key tells the factory how to try a **finished** change for real. It is used only by the [`/qa` run](workflows.md#qa-run), never by a normal ticket, so adding it
changes nothing until somebody comments `/qa` on a ticket or pull request.

```json
"verify": {
  "image": "browser",
  "start": ["dotnet", "run", "--project", "src/Api", "--urls", "http://127.0.0.1:5080"],
  "ready": "http://127.0.0.1:5080/health",
  "startup_timeout_seconds": 90,
  "tests": "e2e",
  "suites": [
    { "name": "api", "command": ["dotnet", "test", "e2e/Api.Tests"], "timeout_seconds": 600 },
    { "name": "ui", "command": ["npx", "playwright", "test"], "cwd": "e2e/ui", "timeout_seconds": 900 }
  ]
}
```

| Key | Required | Default | Meaning and limits |
| --- | --- | --- | --- |
| `tests` | **yes** | | The folder for the end-to-end tests, such as `e2e`. Letters, digits and `_ - @ + /`, no leading `/` or `.` and no `..`. It is a **protected path** for every run except `/qa`: the developer cannot change it |
| `suites` | **yes** | | 1 or 2 suites. Each is a command like a [gate](#reference) (`name`, `command`, `timeout_seconds`, `cwd`), and the `name` must be `api` or `ui` |
| `start` | no | none | The command that starts the application, as an argument list (like a gate's `command`). Leave it out when the tests start the application themselves (for example Playwright's `webServer`) |
| `start_cwd` | no | the repository root | The folder to start the application in |
| `ready` | with `start` | | A local address that answers once the application is up, such as `http://127.0.0.1:5080/health`. Only `http://127.0.0.1:<port>` and `http://localhost:<port>` are allowed, because the application and the tests share one sandbox. Any answer below 500 counts as ready |
| `startup_timeout_seconds` | no | `60` | 5 to 600. How long to wait for `ready` |
| `image` | no | the project's `image` | A sandbox image for the tester and the suites, such as `browser` (Chromium and Playwright) for a `ui` suite |

How a suite runs: the factory starts `start` in the background, polls `ready`, runs the suite's command, and stops the application, all inside one sandbox **without the model
login**, with package registries reachable (so `npm ci` or `dotnet restore` work) and nothing else. If the application stops or does not answer in time, the report says so; if
the suite's exit code is not 0, it failed.

For a `ui` suite:
- Use the `browser` image (`verify.image`). Chromium runs inside the same locked-down sandbox as everything else (read-only root, no capabilities); Playwright starts it without its
  own sandbox, which is its default. Use **`@playwright/test` version 1.52**, the version installed in the image, or the browser will not be found.
- Have the tests write what the page looks like as plain text (visible text, headings, the accessibility tree: `page.locator('body').ariaSnapshot()`) to files under `<tests>/evidence/`.
  The `ux` reviewer reads those; it cannot see screenshots. The folder is removed before anything is pushed.
- Use only the local address. The sandbox has no internet.

The sandbox has **no database or cache server** (see [choosing an image](#choosing-an-image)): the application must run against SQLite or in-memory fakes for a `/qa` run.

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

### More examples

**A cheap, fully automatic project** (a small internal tool): few tries, never asks a person, and the quick process by default.

```json
{
  "image": "python",
  "mode": "quick",
  "limits": { "build_rounds": 2, "clarification_rounds": 0, "revisions_per_pull_request": 2 },
  "gates": [
    { "name": "test", "command": ["python", "-m", "pytest", "-q"] }
  ]
}
```

**A careful project** (customer-facing, with a database): more tries, security review, a run record, and files that only a person may change.

```json
{
  "image": "dotnet",
  "record": true,
  "reviewers": ["reviewer", "security"],
  "limits": { "build_rounds": 4, "revisions_per_pull_request": 5 },
  "protected_paths": ["db/migrations/**", "deploy/**", "global.json"],
  "rules": [
    { "id": "no-binaryformatter", "files": "**/*.cs", "forbid": "BinaryFormatter", "message": "Use System.Text.Json." },
    { "id": "no-console", "files": "src/**/*.cs", "forbid": "Console\\.Write", "message": "Use ILogger.", "severity": "warn" }
  ],
  "gates": [
    { "name": "build", "command": ["dotnet", "build", "--nologo", "-v", "q"] },
    { "name": "test", "command": ["dotnet", "test", "--nologo", "-v", "q"], "timeout_seconds": 900 },
    { "name": "audit", "command": ["dotnet", "list", "package", "--vulnerable"] }
  ]
}
```

What these settings do in a run:

| Setting | In the first example | In the second |
| --- | --- | --- |
| `build_rounds` | after 2 failed tries the run stops with `build_failed` and saves the work to a `-wip` branch | 4 tries before it gives up |
| `clarification_rounds: 0` | an unclear ticket is built on stated assumptions, with no question on the ticket | the default (3 rounds of questions) |
| `revisions_per_pull_request` | after 2 revisions the factory asks a person to take over | 5 |
| `protected_paths` | none | a change touching `db/migrations/` is rejected with "protected file that only a person may change"; the developer is told and must find another way, or ends with `build_blocked` |
| `rules` | none | `BinaryFormatter` in an added line sends the change back; `Console.Write` is only a warning shown to the reviewers |

### Choosing an image

A project names **one** image, and every agent run and every gate for the ticket runs in it. Choose the one that has every toolchain the project's checks need.

| Alias | Toolchains | Use it for |
| --- | --- | --- |
| `dotnet` | .NET SDK | a .NET project with no JavaScript to build or test |
| `python` | Python 3.12, pytest, ruff | a Python project |
| `node` | Node.js 22 LTS, npm, corepack | a JavaScript/TypeScript project |
| `dotnet-node` | .NET SDK + Node.js 22 | a .NET API with a React or other JS front end |
| `python-node` | Python 3.12, pytest, ruff + Node.js 22 | a Python API with a JS front end |
| `full` | .NET SDK + Python 3 + Node.js 22 | anything that needs all three. Large; prefer a smaller image |
| `browser` | `full` + Chromium and Playwright 1.52 | the `ui` suite of [`verify`](#end-to-end-checks-verify); set it as `verify.image`, not as the project's `image` |

In a combined image any gate can call any of its tools. You do not need a different image per gate; use `cwd` to run a gate in a subfolder:

```json
{
  "image": "dotnet-node",
  "gates": [
    { "name": "api-build", "command": ["dotnet", "build", "--nologo", "-v", "q"] },
    { "name": "api-test", "command": ["dotnet", "test", "--nologo", "-v", "q"], "timeout_seconds": 900 },
    { "name": "web-test", "command": ["sh", "-c", "npm ci --no-audit --no-fund && npm test --silent"], "cwd": "frontend", "timeout_seconds": 600 }
  ]
}
```

The combined images must be built once, like the others (`docker compose --profile images build sandbox-image-dotnet-node sandbox-image-python-node sandbox-image-full`). The sandbox has **no Docker and no database or cache server**, in every image: tests that need a live service (Redis, a database server) cannot run there, so write them against in-memory fakes. The agents are told this.

### A project's own image

If none of the standard images fits (a tool the project needs, such as `redis-server` for its tests), the repository can describe its own image in **`.factory/Dockerfile`**. Like `project.json`, it is changed **only by a person**: `.factory/` is protected, and the factory reads the file from the default branch when a run starts, never from what an agent wrote.

This is **off unless the operator turns it on** (`FACTORY_ALLOW_REPO_IMAGES`; see the warning in [Configuration](configuration.md)). Building an image runs the Dockerfile's instructions on the machine's Docker with internet access, so enable it only for repositories whose default branch you control. If the file exists and the switch is off, the ticket says so and the standard image from `project.json` is used.

What the factory accepts (anything else stops the run with `image_failed` and the reason):

| Rule | Why |
| --- | --- |
| The first instruction is exactly `FROM <image>` with one of the factory's own images, for example `FROM dev-foundry/sandbox-dotnet-node:local` | the image then has git, the agent and the entrypoint. No other base, no `AS`, no second `FROM` |
| Only `RUN`, `COPY`, `ADD`, `ENV`, `ARG` and `LABEL` follow | nothing that changes how the container starts: no `ENTRYPOINT`, `CMD`, `USER`, `WORKDIR`, `VOLUME`, `HEALTHCHECK`, `SHELL` or `EXPOSE` |
| `COPY` and `ADD` take files from the context only: no `--from`, and `ADD` may not fetch a URL | fetch with `RUN` and a tool you trust instead |
| No `RUN --mount`, `--network` or `--security`, and no `# syntax=` line | no build frontends or extra privileges |
| The context is `.factory/docker/` (at most 50 files and 1 MB, no symbolic links) next to a Dockerfile of at most 64 KB | repository code never gets into the build, so `COPY . .` cannot pull in secrets |

It is built by the launcher with a memory and CPU limit, a time limit of 30 minutes and a size limit of 6 GB. The image is named after a hash of the Dockerfile, its context and the base image, so an unchanged file is built **once** and reused by every ticket; a change (or a rebuilt base image) builds a new one. Every agent and every gate for the ticket then runs in it. The first build can take minutes; the ticket says when it starts and when it is ready. If the build fails there is no silent fallback: the run stops with the build output, because the checks would otherwise run in an environment the project did not intend.

An example that adds a Redis server for tests is in [docs/examples](examples/README.md). The `image` in `project.json` is still required; it is the one used when the Dockerfile is ignored. Old project images are not removed automatically: list them with `docker image ls dev-foundry/repo` and remove ones you no longer need with `docker image rm`.

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

**Starting points** (adapt the paths; each pattern runs on added lines only):

- **Python**, `files: "**/*.py"`: `forbid: "\\b(pickle\\.loads?|eval|exec)\\(|yaml\\.load\\("` (unsafe deserialisation and dynamic code), and `forbid: "shell\\s*=\\s*True"` (shell injection).
- **.NET**, `files: "**/*.cs"`: `forbid: "BinaryFormatter|AllowAnyOrigin\\(\\)"` (unsafe deserialisation, wide-open CORS).
- **Node and UI**, `files: ["**/*.js", "**/*.ts", "**/*.tsx"]`: `forbid: "dangerouslySetInnerHTML|\\beval\\(|new Function\\("` (script injection).
- Credentials in code are already caught by the factory's built-in secret scan, so no rule is needed for them.

Dependency problems are better caught by a tool than a pattern: add an audit as a gate, for example `{ "name": "audit", "command": ["pip-audit"] }`, `["npm", "audit", "--omit=dev"]` or `["dotnet", "list", "package", "--vulnerable"]`. Note that a gate runs with package registries reachable and nothing else.

### Limits

`limits` sets how much effort a run may spend on this project. Every key is optional; a missing key means the factory's default, which the operator sets in `infra/.env` ([`FACTORY_DEFAULT_*`](configuration.md)). A value outside its range, a value that is not a whole number, or a misspelled key makes the file unusable, so a typo cannot silently do nothing.

| Key | Range | Factory default | Meaning |
| --- | --- | --- | --- |
| `build_rounds` | 1 to 5 | `3` | How many times the developer (or the pull request resolver) may try before the run gives up with `build_failed`. Each round is a developer run plus the checks and the reviews, so this is the main token knob |
| `clarification_rounds` | 0 to 4 | `3` | How many rounds of questions to a person a stage may use. `0` means the factory never asks: every stage decides with reasonable assumptions and states them |
| `revisions_per_pull_request` | 1 to 10 | `3` | How many [revisions](workflows.md#pull-request-revisions) one pull request may get before a person has to take over |

```json
"limits": { "build_rounds": 2, "clarification_rounds": 0 }
```

A project cannot go beyond these ranges: the ceilings belong to the factory.

### Protected paths

`protected_paths` adds files the agents may never change, on top of the built-in list (`.factory/`, `.github/`, `.forgejo/`, `.gitea/`, `.git/` and the CI pipeline files). A change that touches one is rejected the same way, and the developer is told so. Use it for what only a person should edit: database migrations, deployment configuration, the SDK version, and the lint and test settings (so no agent can loosen a check to make it pass).

```json
"protected_paths": ["db/migrations/**", "deploy/**", "ruff.toml"]
```

Up to 20 patterns, with the same syntax as the `ui` paths below (`*` within a folder, `**` across folders, a pattern without a slash matches the file name anywhere), compared without regard to case. A project can only **add** protection; it cannot remove any. A pattern that matches too much, such as `**`, makes every change fail, so keep them specific.

**Do not protect a dependency file on its own.** A lockfile (`package-lock.json`, `poetry.lock`) changes together with its manifest (`package.json`, `pyproject.toml`) whenever a ticket needs a new package. If you protect only one, that change becomes impossible: the manifest can change but the lockfile cannot, and `npm ci` then fails because the two disagree. Choose one policy:
- **Agents may add dependencies** (the usual choice): protect neither. Both files change in the diff, the reviewers see why, and the `npm ci` (or equivalent) check catches the two disagreeing.
- **Agents may never add dependencies**: protect **both**, for example `["package.json", "package-lock.json"]`. A ticket that needs a package then stops, and a person adds it.

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

Ready-made examples for a .NET and a React project are in [docs/examples](examples/README.md), and complete working starter projects are in [templates](../templates/README.md). Suggested outline (short headings, a few lines each; the agents read the whole file on every run, so every line costs tokens):

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

To run a ticket again, comment `/retry` on it, or **close and reopen** it. To follow a run in detail, open it in the Temporal UI; the [operations guide](operations.md) lists what to look at.

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
| "The repository ... is empty: it has no commits yet" | a brand-new repository has nothing to branch from. Make a first commit on the default branch (a README and `.factory/project.json`), then close and reopen the ticket |
| a gate fails with "command not found" | the tool is not in the sandbox image: install it in the gate, or add it to the image ([launcher guide](../services/sandbox-launcher/README.md#images)) |
| the change is rejected as "build output" | add the generated folder to `.gitignore` |
| the run stops with `build_blocked` | the ticket needs a protected file changed ([above](#what-the-factory-will-not-do)) |
| a pull request has a surprising extra file | review it, then merge or ask for removal; the PR lists each file |
