# Configuration reference

[Documentation index](README.md) | [Getting started](getting-started.md) | [Project settings](project-setup.md#reference)

Where each setting lives, whether it is required, and what it defaults to. Three places configure the factory:

| Where | What it configures | Who edits it |
| --- | --- | --- |
| [`infra/.env`](#infraenv) | the stack: passwords, ports, paths, network mode | the operator (you) |
| [`agents/`](#agents-and-models) | which models and permissions each agent role has, and the prompts | the operator, reviewed like code |
| [`.factory/project.json`](project-setup.md#reference) in each repository | the image and checks for that project | the repository's owners |

The repository file can only choose among what the operator already offers; it cannot widen permissions, models, images or network access.

## `infra/.env`

Copy `infra/.env.example` to `infra/.env` (`cp -n`, `chmod 600`). The file is ignored by Git. After changing it, run `docker compose up -d` from `infra/`
(Compose recreates the services whose settings changed).

### Required

| Key | What | How to get it |
| --- | --- | --- |
| `POSTGRES_PASSWORD`, `FORGEJO_DB_PASSWORD`, `TEMPORAL_DB_PASSWORD`, `PGADMIN_DEFAULT_PASSWORD` | database and admin passwords; changing them later does **not** update existing databases | `openssl rand -hex 24`, all different |
| `PGADMIN_DEFAULT_EMAIL` | pgAdmin login | any email |
| `FORGEJO_WEBHOOK_SECRET` | the shared secret of the webhook signature; at least 32 characters | `openssl rand -hex 32` |
| `FACTORY_WORKSPACE_ROOT` | absolute host path of the checkouts, the same path inside every container | `mkdir -p` it first, owned by you |
| `FACTORY_UID`, `FACTORY_GID` | the host user that owns the workspaces; the controller and the sandboxes run as it. Must not be root | `id -u`, `id -g` |
| `DOCKER_GID` | the group of the Docker socket, so the launcher does not run as root | `stat -c %g /var/run/docker.sock` |

### Needed for the factory to work, but optional for the stack to start

| Key | What | Default |
| --- | --- | --- |
| `FORGEJO_BOT_LOGIN` | the bot account's username. The factory revises only pull requests this account opened, and compares the author with it (the token's own scopes do not allow asking Forgejo) | `factory-bot` |
| `FACTORY_DEFAULT_BUILD_ROUNDS` | how many times the developer may try before the run gives up, for projects whose [`limits`](project-setup.md#limits) do not set `build_rounds`. 1 to 5 | `3` |
| `FACTORY_DEFAULT_CLARIFICATION_ROUNDS` | how many rounds of questions to a person a stage may use (`0` = never ask, decide alone), for projects without `limits.clarification_rounds`. 0 to 4 | `3` |
| `FACTORY_DEFAULT_REVISIONS_PER_PULL_REQUEST` | how many revisions one pull request may get, for projects without `limits.revisions_per_pull_request`. 1 to 10 | `3` |
| `FORGEJO_BOT_TOKEN` | the factory bot's access token (scopes `write:issue`, `write:repository`). Only the controller receives it. Empty disables all ticket actions | empty |
| `FACTORY_GLOBAL_CONFIG` | absolute path of the repository's `agents/` folder, mounted read-only into every sandbox. Empty means sandboxes run **without** role files and the agents cannot work. The controller also mounts it read-only, only to read each role's model name | empty (the controller falls back to `../agents`) |

**Example: tune the defaults for every project at once** (in `infra/.env`, then `docker compose up -d --force-recreate controller`). A project's own `limits` in `.factory/project.json` still win, within the same ranges:

```bash
FACTORY_DEFAULT_BUILD_ROUNDS=2            # spend fewer tokens: two tries, then stop
FACTORY_DEFAULT_CLARIFICATION_ROUNDS=1    # ask a person at most once per stage
FACTORY_DEFAULT_REVISIONS_PER_PULL_REQUEST=5
```

### Optional

| Key | What | Default |
| --- | --- | --- |
| `SANDBOX_EGRESS` | how strictly the sandboxes' internet access is limited: `enforce` (only each profile's allowlisted hosts through the proxy), `audit` (every HTTPS host allowed and logged), `open` (no restrictions at all). Anything else stops the launcher and proxy from starting. After a change: `docker compose up -d --force-recreate egress-proxy sandbox-launcher` | `enforce` |
| `EGRESS_PROFILES` | your own allowlist file instead of `egress/profiles.json` (path relative to `infra/`, or absolute) | `../egress/profiles.json` |
| `SANDBOX_NET_BASE` | the first two numbers of the internal sandbox networks (`10.250.1.0/24`, `.2.0/24`, `.3.0/24`). Change only if they clash with a network you use | `10.250` |
| `TEMPORAL_ADDRESS`, `TEMPORAL_NAMESPACE`, `TEMPORAL_TASK_QUEUE` | shared Temporal connection settings; the api and controller must agree on the namespace and queue | `temporal:7233`, `default`, `factory-events` |
| `TEMPORAL_RETENTION` | how long Temporal keeps a finished workflow's history, which is also the window for duplicate-delivery detection and for reading a run's full record. Applied only when the namespace is **first created** | `7d` |
| `TEMPORAL_UI_URL` | how a person opens the Temporal UI; used for the trace link in ticket comments | `http://localhost:<TEMPORAL_UI_PORT>` |
| `SMTP_HOST`, `SMTP_PORT` | Forgejo's mail server (Mailpit by default; mail is not delivered externally) | `mailpit`, `1025` |
| `COMPOSE_PROJECT_NAME` | the Compose project name; it prefixes the network and volume names (`dev-foundry_opencode-data`, ...) | `dev-foundry` |

### Host ports (all bind to localhost only)

| Key | Service | Default |
| --- | --- | --- |
| `FORGEJO_HTTP_PORT` / `FORGEJO_SSH_PORT` | Forgejo web / SSH | `3000` / `2222` |
| `CONTROLLER_API_PORT` | the webhook api (`/health/live`, `/health/ready`, `/webhooks/forgejo`, OpenAPI document) | `8000` |
| `TEMPORAL_PORT` / `TEMPORAL_UI_PORT` | Temporal gRPC / web UI | `7233` / `8080` |
| `POSTGRES_PORT` | PostgreSQL | `5432` |
| `PGADMIN_PORT` | pgAdmin | `5050` |
| `MAILPIT_UI_PORT` / `MAILPIT_SMTP_PORT` | Mailpit inbox / SMTP | `8025` / `1025` |

Services inside Docker always use the standard ports; host port overrides only change what you open from your machine. Temporal and Mailpit have no
authentication, which is why everything binds to localhost.

## Service settings

These are read by the services themselves. Most are set for you in `infra/compose.yaml`; the rest keep their defaults and can be changed by adding the variable
to that service's `environment` block in `infra/compose.yaml` (they are not in `.env`).

### api

| Variable | Default | Meaning |
| --- | --- | --- |
| `FORGEJO_WEBHOOK_SECRET` | none (required, 32+ characters) | the HMAC key for webhook signatures |
| `TEMPORAL_ADDRESS`, `TEMPORAL_NAMESPACE`, `TEMPORAL_TASK_QUEUE` | `localhost:7233`, `default`, `factory-events` | where to start workflows |
| `MAX_WEBHOOK_BYTES` | `1048576` | largest accepted webhook body (1 to 1,048,576) |

### controller

| Variable | Default | Meaning |
| --- | --- | --- |
| `TEMPORAL_ADDRESS`, `TEMPORAL_NAMESPACE`, `TEMPORAL_TASK_QUEUE` | as above | the queue the workflows run on |
| `FORGEJO_URL` | `http://forgejo:3000` | where the controller reaches Forgejo (over the `forge` network) |
| `FORGEJO_BOT_TOKEN` | empty | the bot's token |
| `FORGEJO_BOT_LOGIN` | `factory-bot` | the bot's username, used to recognise the factory's own pull requests |
| `FACTORY_DEFAULT_BUILD_ROUNDS`, `FACTORY_DEFAULT_CLARIFICATION_ROUNDS`, `FACTORY_DEFAULT_REVISIONS_PER_PULL_REQUEST` | `3`, `3`, `3` (set by Compose) | defaults for a project's `limits`; a value outside its range stops the controller at start with a message |
| `FACTORY_WORKSPACE_ROOT` | none (required) | where checkouts live |
| `FACTORY_AGENTS_DIR` | `/etc/factory` (set by Compose) | the read-only agents folder, used only to name the model per role in run records |
| `TEMPORAL_UI_URL` | `http://localhost:8080` | base of the trace link in comments |

### sandbox-launcher

| Variable | Default | Meaning |
| --- | --- | --- |
| `SANDBOX_IMAGES` | six images (`dotnet`, `python`, `node`, `dotnet-node`, `python-node`, `full`), set in Compose | the allowlist of image aliases: `alias=image;alias=image`. **Add a language by adding a pair** (and a `sandboxes/<name>/Dockerfile`) |
| `SANDBOX_NETWORKS`, `SANDBOX_PROXIES` | set in Compose | which internal network and proxy address each egress profile uses; both must list the same profiles |
| `SANDBOX_EGRESS_MODE` | `enforce` (from `SANDBOX_EGRESS`) | `enforce`, `audit` or `open` |
| `SANDBOX_OPEN_NETWORK` | `dev-foundry_sandbox-open` | the ordinary network used in `open` mode |
| `SANDBOX_AUTH_VOLUME` | `dev-foundry_opencode-data` (set in Compose) | the volume holding the OpenCode login. Empty disables model runs |
| `SANDBOX_AUTH_READONLY` | `false` | `true` forbids an agent from changing the login, at the cost of a login that stops working when its token expires |
| `FACTORY_GLOBAL_CONFIG` | from `.env` | the agents folder mounted at `/etc/factory` |
| `SANDBOX_UID`, `SANDBOX_GID` | `1000` | the user the sandbox runs as (from `FACTORY_UID`/`FACTORY_GID`); must not be root |
| `SANDBOX_MEMORY_BYTES` | 2 GiB | memory limit per sandbox (swap equal, so no swapping) |
| `SANDBOX_CPUS` | `2` | CPU limit per sandbox |
| `SANDBOX_PIDS_LIMIT` | `256` | process limit per sandbox |
| `SANDBOX_MAX_RUN_SECONDS` | `1800` | the longest a single run may take; a request's own timeout is capped by it |
| `SANDBOX_MAX_CONCURRENT` | `2` | sandboxes running at once; others wait |
| `SANDBOX_TASK_QUEUE` | `sandbox` | the Temporal queue the launcher polls |
| `DOCKER_SOCKET` | `/var/run/docker.sock` | where the launcher reaches Docker |

What the launcher fixes for **every** sandbox, whatever a request says: non-root user, read-only root filesystem with a 1 GB `/tmp`, all capabilities dropped, `no-new-privileges`, no
privileged mode, no devices, an init process, the memory, CPU and process limits above, and no mounts except the workspace, the agents folder (read-only) and, for model runs, the
login volume. Details: [launcher guide](../services/sandbox-launcher/README.md).

### egress-proxy

| Variable | Default | Meaning |
| --- | --- | --- |
| `EGRESS_PROFILES_FILE` | `/etc/egress/profiles.json` (mounted from `EGRESS_PROFILES`) | the allowlist file; the proxy refuses to start if it cannot parse it |
| `EGRESS_LISTEN_MAP` | set in Compose | which profile each listen address belongs to |
| `EGRESS_MODE` | `enforce` (from `SANDBOX_EGRESS`) | `enforce`, `audit`, `open` (open bypasses the proxy entirely) |
| `EGRESS_PORT` | `3128` | the proxy's port |

The allowlist format, adding a host, and what the protection does and does not cover are in the [egress guide](../egress/README.md).

## Agents and models

Everything about the agents is in [`agents/`](../agents/README.md): one Markdown file per role in `agents/agents/`, and `agents/opencode.json` for the shared defaults. A change takes
effect on the next agent run; no rebuild or restart.

A role file starts with front matter:

```yaml
---
description: Implements a change in the checked-out repository and makes the project's checks pass
mode: primary
model: openai/gpt-6.1-sol
permission:
  edit: allow
  bash: allow
---
```

| Key | Meaning |
| --- | --- |
| `model` | the model for this role. `opencode.json` has a default for roles that leave it out. Today every role uses `openai/gpt-6.1-sol` |
| `variant` | OpenCode's reasoning preset for the model, for example `low` or `high`; which values exist depends on the model |
| `reasoningEffort`, `temperature` | passed through to the provider; accepted by OpenCode in a role file (checked with `opencode debug agent <role>`) |
| `permission` | what the role may do. The baseline in `opencode.json` is deny-all except reading (`read`, `glob`, `grep`); a role adds `edit: allow` and/or `bash: allow` |

Which roles can do what:

| Role | Used in | Reads | Edits files | Runs commands | Network profile |
| --- | --- | --- | --- | --- | --- |
| `analyst`, `coverage`, `architect` | feature | yes | no | no | `model` |
| `developer` | feature, bug | yes | yes | yes | `developer` (model + package registries) |
| `reviewer`, `security`, `ux`, `qa` | feature, bug (QA: feature) | yes (given the diff) | no | no | `model` |
| `bug-analyst`, `diagnostician` | bug | yes | no | no | `model` |
| `reproducer` | bug | yes | yes (tests only, checked) | yes | `developer` |
| `pr-resolver` | pull request revision | yes | yes (checked like the developer's changes) | yes | `developer` |
| `incident-analyst`, `mitigator` | incident | yes | no | no | `model` |
| `investigator` | incident | yes (and git history) | no | yes (read-only workspace) | `model` |
| `postmortem-writer` | incident | yes | yes (one new file, checked) | no | `model` |

The project's checks run with the `packages` profile (registries only, no model), and the factory's own small helper commands (does this file exist, a commit exists, a file hash) run with no network.
Roles are chosen in code, never by a repository or an agent.

Choosing a model: check it works with your login first (`opencode models openai` from the [login guide](opencode-login.md); a refused model makes that role fail with an unclear
"exited with code" message). Re-check the role after a change: prompts and the strict JSON answers were tuned on one model. Reasoning tokens and cost per role appear in each
[run record](workflows.md#the-run-record), so you can compare settings. See the [agents guide](../agents/README.md#choosing-the-model-per-role).

## Fixed limits (not configurable)

Build rounds (3), question rounds, retry counts, the 7-day wait, file and size limits and agent timeouts are constants in the controller code. The table is in
[Workflows](workflows.md#limits-and-timeouts).
