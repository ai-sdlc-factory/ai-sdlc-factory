# Configuration reference

[Documentation index](README.md) | [Getting started](getting-started.md) | [Project settings](project-setup.md#reference)

Where each setting lives, whether it is required, and what it defaults to. Three places configure the factory:

| Where | What it configures | Who edits it |
| --- | --- | --- |
| [`deploy/.env`](#deployenv) | the stack: passwords, ports, paths, network mode | the operator (you) |
| [`agents/`](#agents-and-models) | which models and permissions each agent role has, and the prompts | the operator, reviewed like code |
| [`.factory/project.json`](project-setup.md#reference) in each repository | the image and checks for that project | the repository's owners |

The repository file can only choose among what the operator already offers; it cannot widen permissions, models, images or network access.

## `deploy/.env`

Copy `deploy/.env.example` to `deploy/.env` (`cp -n`, `chmod 600`). The file is ignored by Git. After changing it, run `docker compose up -d` from `deploy/`
(Compose recreates the services whose settings changed).

### Required

| Key | What | How to get it |
| --- | --- | --- |
| `COMPOSE_FILE` | optional: `compose.yaml:compose.postgres.yaml` runs Temporal on PostgreSQL instead of its default SQLite file | unset |
| `POSTGRES_PASSWORD`, `TEMPORAL_DB_PASSWORD` | PostgreSQL option only: the database passwords; changing them later does **not** update existing databases | `openssl rand -hex 24`, both different |
| `PGADMIN_DEFAULT_EMAIL`, `PGADMIN_DEFAULT_PASSWORD` | optional: the login of pgAdmin, a database browser that only starts with `docker compose --profile tools up -d pgadmin`; set both first | any email; `openssl rand -hex 24` |
| `FACTORY_HOSTS` | the git hosts the factory serves, as one line of JSON, **the one place** that says where they are and how to reach them (see [FACTORY_HOSTS](#factory_hosts) below) | written by hand; secrets with `openssl rand -hex 32` |
| `FACTORY_WORKSPACE_ROOT` | absolute host path of the checkouts, the same path inside every container | `mkdir -p` it first, owned by you |
| `FACTORY_UID`, `FACTORY_GID` | the host user that owns the workspaces; the controller and the sandboxes run as it. Must not be root | `id -u`, `id -g` |
| `DOCKER_GID` | the group of the Docker socket, so the launcher does not run as root | `stat -c %g /var/run/docker.sock` |

### FACTORY_HOSTS

The git hosts the factory serves, as one line of JSON in `deploy/.env`. How to get each value on Forgejo and GitHub (the bot account, the token and its permissions, the webhook and its events) is in [Connecting a git host](git-hosts.md). The factory itself knows nothing about any one host; Forgejo is only the first kind it was taught. The Forgejo of the [`forgejo/`](../deploy/forgejo/README.md) folder is a separate Compose project (with its own settings) that you list here like any other host.

```bash
FACTORY_HOSTS=[{"name":"forgejo","url":"http://forgejo:3000","token":"...","bot_login":"factory-bot","webhook_secret":"..."},{"name":"github","type":"github","url":"https://github.com","token":"...","bot_login":"my-bot","webhook_secret":"..."}]
```

| Field | What | Default |
| --- | --- | --- |
| `name` | lower case letters, digits and `-`, starting with a letter, at most 31. It is the end of the host's webhook route (`/webhooks/<name>`) and the qualifier of its repositories: they are named `<name>:owner/repo` in runs, folders and logs. A host called `forgejo` keeps plain `owner/repo`, the way the factory has always named them, so renaming nothing | required |
| `url` | where the host is, as the controller reaches it: `http://forgejo:3000` for the bundled Forgejo, `https://github.com`, or the address of a GitHub Enterprise Server | required |
| `type` | `forgejo` or `github` | `forgejo` |
| `token` | the bot's access token on that host (Forgejo scopes `write:issue`, `write:repository`; GitHub: see [Several git hosts](operations.md#several-git-hosts)). Only the controller needs it to act; empty disables all ticket actions on that host | empty |
| `bot_login` | the bot account's username. The factory revises only pull requests this account opened, and compares the author with it (the token's own scopes do not allow asking the host) | `factory-bot` |
| `webhook_secret` | the HMAC key of that host's webhook signature, 32+ characters, the same one you paste into each repository's webhook | required by the api |
| `triggers` | how its tickets say which workflow to run: `{"labels":{"feature":["enhancement"]},"item_types":{"bug":["Bug"]},"command":true}`. `labels` and `item_types` add names per kind (`incident`, `plan`, `feature`, `bug`, `docs`; the plain labels always work; at most 20 names of at most 50 characters each); `command: true` turns on `/factory <kind>` in a comment on a ticket that has no kind. **Turning the command on lets anyone who can comment start a run**, where only someone who can label could before | the plain labels |
| `commands` | who may steer the factory on this host's repositories with a comment (a slash command, an answer to an agent) or a review that asks for a revision. A level as text, or an object: `"write"` (anyone with write access to the repository), `"admin"`, `"none"` (only the people named), `"anyone"` (the default), or `{"who":"write","allow":["alice"],"deny":["mallory"],"refusal":"silent"}`. `allow` names people let in whatever their access, `deny` people always refused, even administrators (deny first, then allow, then the level); logins are the host's own, written the way the host writes them (case does not matter). `refusal` is `"comment"` (the default: one short note on the ticket that names the rule, never the person) or `"silent"`. `each` gives single commands a rule on top of this one (`{"approve": "admin", "hotfix": {"who": "none", "allow": ["alice"]}}`): a person has to pass both, so it can only be stricter; the names are `ask`, `setup`, `retry`, `fix`, `update`, `qa`, `review`, `factory`, `approve`, `option`, `hotfix`, `resolved`, `reply` (an answer to an agent) and `changes` (a review that asks for changes). `project: true` lets the repository's own [`commands`](project-setup.md#who-may-steer-commands) key tighten the rule further (it is read from `.factory/project.json`, one request per burst of commands). Starting a run by a label is not covered: the host already limits who may label | anyone |
| `repositories` | rules for single repositories of this host, `{"owner/name":{"commands":{...}}}`, which replace the host's own `commands` there (they can loosen as well as tighten: they are yours, not the repository's) | none |

**Who may steer the factory** (`commands`, `repositories`) is optional, and with nothing set anyone who can comment may use commands, as the factory always worked. On a public repository you will want a rule, because anyone can comment there. Examples:

```bash
# Only you and a colleague on the public GitHub repository (and /factory <kind> on, so you can start a run on a stranger's issue by a comment)
{"name":"github","type":"github","url":"https://github.com","token":"...","webhook_secret":"...","triggers":{"command":true},"commands":{"who":"none","allow":["you","colleague"]}}

# The team on a Forgejo (anyone with write access), but only two people on one public repository
{"name":"forgejo","url":"http://forgejo:3000","token":"...","webhook_secret":"...","commands":"write","repositories":{"acme/public-site":{"commands":{"who":"none","allow":["you","colleague"]}}}}

# Write access, one outside contributor let in, one person blocked
"commands":{"who":"write","allow":["outside-contributor"],"deny":["mallory"]}

# Anyone with write access, but /approve and /hotfix (which open tickets and carry out incident advice) only for administrators, and the project may tighten it further
"commands":{"who":"write","each":{"approve":"admin","hotfix":"admin"},"project":true}
```

The rule covers every comment that steers a run: the slash commands (`/fix`, `/qa`, `/review`, `/update`, `/retry`, `/ask`, `/setup`, `/approve`, `/hotfix`, `/option` and the rest), `/factory <kind>`, an answer to an agent's question, and a review that asks for changes (the reviewer's login is checked). It does not cover a label, which the host limits itself. For `write` and `admin` the controller asks the host what the person may do (Forgejo and GitHub: the collaborator permission. GitHub's needs a token with write access to the repository; Forgejo answers about other people only to a repository administrator, so the bot has to be one, or the answer is "cannot say", which refuses. A rule that only names people needs none of this; a host that cannot say makes the answer no) and keeps the answer for [`FACTORY_PERMISSION_CACHE_SECONDS`](#needed-for-the-factory-to-work-but-optional-for-the-stack-to-start). A rule that only names people (`none`, `allow`, `deny`) never asks the host. The controller logs each host's rule when it starts.

At least one host is needed. A setting that cannot be used stops the api and the controller at start, with a message that names the host and the problem and never prints a secret. **Every service that reads the list receives the tokens and secrets in it**; the api needs only the secrets, and the controller only the rest.

### Needed for the factory to work, but optional for the stack to start

| Key | What | Default |
| --- | --- | --- |
| `FACTORY_PERMISSION_CACHE_SECONDS` | how long what a git host said a person may do on a repository is kept, where a host's [`commands`](#factory_hosts) rule asks for access (`write` or `admin`). A burst of commands from one person then asks the host once. `0` asks every time; at most 3600. A person whose access was just taken away still passes until the answer expires. A host that fails or cannot say is never remembered: the person is refused for now | `300` |
| `FACTORY_GIT_AUTHOR_NAME`, `FACTORY_GIT_AUTHOR_EMAIL` | the name and e-mail address on the commits the factory makes. To have a host link them to the bot's account, use that account's address (on GitHub its noreply address, `<id>+<login>@users.noreply.github.com`; the default address belongs to no account, so GitHub shows the commits without a profile). One line, no `<` or `>`; anything else stops the controller at start | `AI SDLC Factory`, `factory-bot@ai-sdlc-factory.test` |
| `FACTORY_DEFAULT_BUILD_ROUNDS` | how many times the developer may try before the run gives up, for projects whose [`limits`](project-setup.md#limits) do not set `build_rounds`. 1 to 5 | `3` |
| `FACTORY_DEFAULT_CLARIFICATION_ROUNDS` | how many rounds of questions to a person a stage may use (`0` = never ask, decide alone), for projects without `limits.clarification_rounds`. 0 to 4 | `3` |
| `FACTORY_DEFAULT_REVISIONS_PER_PULL_REQUEST` | how many revisions one pull request may get, for projects without `limits.revisions_per_pull_request`. 1 to 10 | `3` |
| `FACTORY_IMPROVEMENT` | `false` switches off the weekly [report on what repeats](operations.md#the-weekly-report-what-repeats) (it only reads the factory's own logs and writes a file under the workspace root) | `true` |
| `FACTORY_IMPROVEMENT_REPO` | a repository as `owner/name` (on the host called `forgejo`) or `host:owner/name` (any other host, for example `github:acme/factory`; usually the factory's own repository) where the weekly report is opened as a ticket, with an agent's proposals for changing the factory as a comment. It needs a `.factory/project.json`. Empty = the report is only saved to a file. Anything else stops the controller at start. Nothing is ever applied automatically | empty |
| `FACTORY_EGRESS_DENIED_LOG` | where the controller reads the egress proxy's log of refused hosts (set by Compose to the shared `egress-log` volume). Missing file = the report says it knows nothing about refused hosts | `/var/log/egress/denied.jsonl` (set by Compose) |
| `FACTORY_CONFIRM_OVER_TOKENS` | a `/qa` run whose [estimate](operations.md#estimates-and-what-a-stopped-run-used) (the median of the repository's last 9 runs of that kind) is over this many tokens waits for `/qa go` on the same ticket before it spends anything. A project can set its own [`limits.confirm_over`](project-setup.md#limits). `0` or empty = never wait; otherwise a whole number from 10000 | `0` |
| `FACTORY_MAX_TOKENS_PER_RUN` | the most tokens one run may use before it stops (see [Token limits](operations.md#token-limits)). A project can set a lower [`limits.token_budget`](project-setup.md#limits), never a higher one. `0` or empty = no limit; otherwise a whole number from 10000 | `0` |
| `FACTORY_DAILY_TOKEN_LIMIT` | the most tokens all runs together may use in one UTC day, counted from the [usage log](operations.md#the-usage-log-totals-across-tickets-days-and-repositories) plus what the asking run has used itself. A run that would go over **waits for the next day** and says so on its ticket. `0` or empty = no limit | `0` |
| `FACTORY_REPO_DAILY_TOKEN_LIMIT` | the same for one repository (`owner/name`). `0` or empty = no limit | `0` |
| `FACTORY_ALLOW_REPO_IMAGES` | `true` lets a repository bring its own sandbox image: a `.factory/Dockerfile` that the launcher builds once per distinct content, starting from one of the factory's own images ([the rules](project-setup.md#a-projects-own-image)). **Off by default.** Turning it on means **anyone who can merge to a repository's default branch can run build instructions on this machine's Docker, with internet access** (the egress proxy covers running sandboxes, not builds). Treat it like running that repository's CI, and enable it only for repositories you control. The controller and the launcher read the same setting | `false` |
| `FACTORY_JANITOR` | `false` switches the [janitor](operations.md#cleaning-up) off. It runs a few minutes after the controller starts and then every `FACTORY_JANITOR_INTERVAL_HOURS` | `true` |
| `FACTORY_JANITOR_INTERVAL_HOURS` | how often the janitor runs, 1 to 168 | `24` |
| `FACTORY_JANITOR_CHECKOUT_DAYS` | a checkout is removed only if its run is no longer running **and** nothing has touched it for this many days, 1 to 30 | `2` |
| `FACTORY_JANITOR_WIP_DAYS` | a work-in-progress branch (`factory/issue-<n>-wip`) is listed in the controller log once its last commit is this many days old, 1 to 365 | `30` |
| `FACTORY_JANITOR_DELETE_WIP` | `true` lets the janitor **delete** those branches, but only when their ticket is closed and no open pull request uses them. Off: they are only listed | `false` |
| `FACTORY_MODEL` | the model every agent uses, as OpenCode writes it: `provider/model` (for example `openai/gpt-6.1-sol`). Empty keeps the default in `agents/opencode.json`. The launcher gives it to every run that calls the model, as OpenCode's inline configuration, which ranks above the file; the controller records it. A role file that names its own `model:` keeps that one. It must be one your login or key can use, on a provider `agents/opencode.json` enables (`enabled_providers`) and the [egress allowlist](../src/egress-proxy/ALLOWLIST.md) reaches (see [other ways to sign in](opencode-login.md#other-ways-to-sign-in)). A value that is not `provider/model` stops the controller and the launcher at start | empty (the default in `opencode.json`) |
| `FACTORY_GLOBAL_CONFIG` | absolute path of the repository's `agents/` folder, mounted read-only into every sandbox. Empty means sandboxes run **without** role files and the agents cannot work. Point it at the repository's own folder: a **copy** must be refreshed after every update, because a role added since the copy was made is missing and its agent then runs without its instructions (the run stops with "the role file ... is missing"). The controller also mounts it read-only, only to read each role's model name | empty (the controller falls back to `../agents`) |

**Example: tune the defaults for every project at once** (in `deploy/.env`, then `docker compose up -d --force-recreate controller`). A project's own `limits` in `.factory/project.json` still win, within the same ranges:

```bash
FACTORY_DEFAULT_BUILD_ROUNDS=2            # spend fewer tokens: two tries, then stop
FACTORY_DEFAULT_CLARIFICATION_ROUNDS=1    # ask a person at most once per stage
FACTORY_DEFAULT_REVISIONS_PER_PULL_REQUEST=5
```

### Optional

| Key | What | Default |
| --- | --- | --- |
| `SANDBOX_EGRESS` | how strictly the sandboxes' internet access is limited: `enforce` (only each profile's allowlisted hosts through the proxy), `audit` (every HTTPS host allowed and logged), `open` (no restrictions at all). Anything else stops the launcher and proxy from starting. After a change: `docker compose up -d --force-recreate egress-proxy sandbox-launcher` | `enforce` |
| `EGRESS_PROFILES` | your own allowlist file instead of `src/egress-proxy/profiles.json` (path relative to `deploy/`, or absolute) | `../src/egress-proxy/profiles.json` |
| `SANDBOX_NET_BASE` | the first two numbers of the internal sandbox networks (`10.250.1.0/24`, `.2.0/24`, `.3.0/24`). Change only if they clash with a network you use | `10.250` |
| `TEMPORAL_ADDRESS`, `TEMPORAL_NAMESPACE`, `TEMPORAL_TASK_QUEUE` | shared Temporal connection settings; the api and controller must agree on the namespace and queue | `temporal:7233`, `default`, `factory-events` |
| `TEMPORAL_RETENTION` | how long Temporal keeps a finished workflow's history, which is also the window for duplicate-delivery detection and for reading a run's full record. Applied only when the namespace is **first created** | `7d` |
| `TEMPORAL_UI_URL` | how a person opens the Temporal UI; used for the trace link in ticket comments | `http://localhost:<TEMPORAL_UI_PORT>` |
| `COMPOSE_PROJECT_NAME` | the Compose project name; it prefixes the network and volume names (`ai-sdlc-factory_opencode-data`, ...) | `ai-sdlc-factory` |

### Host ports (all bind to localhost only)

| Key | Service | Default |
| --- | --- | --- |
| `CONTROLLER_API_PORT` | the webhook api (`/health/live`, `/health/ready`, `/webhooks/forgejo`, OpenAPI document) | `8000` |
| `TEMPORAL_PORT` / `TEMPORAL_UI_PORT` | Temporal gRPC / web UI | `7233` / `8080` |
| `POSTGRES_PORT` | PostgreSQL | `5432` |
| `PGADMIN_PORT` | pgAdmin (optional, `--profile tools`) | `5050` |

Services inside Docker always use the standard ports; host port overrides only change what you open from your machine. Temporal has no
authentication, which is why everything binds to localhost.

## Service settings

These are read by the services themselves. Most are set for you in `deploy/compose.yaml`; the rest keep their defaults and can be changed by adding the variable
to that service's `environment` block in `deploy/compose.yaml` (they are not in `.env`).

### api

| Variable | Default | Meaning |
| --- | --- | --- |
| `FACTORY_HOSTS` | none (required) | the git hosts (see above): the api reads each one's name, kind and `webhook_secret` (32+ characters, the HMAC key of its webhook signature) and serves `/webhooks/<name>` |
| `TEMPORAL_ADDRESS`, `TEMPORAL_NAMESPACE`, `TEMPORAL_TASK_QUEUE` | `localhost:7233`, `default`, `factory-events` | where to start workflows |
| `MAX_WEBHOOK_BYTES` | `1048576` | largest accepted webhook body (1 to 1,048,576) |

### controller

| Variable | Default | Meaning |
| --- | --- | --- |
| `TEMPORAL_ADDRESS`, `TEMPORAL_NAMESPACE`, `TEMPORAL_TASK_QUEUE` | as above | the queue the workflows run on |
| `FACTORY_HOSTS` | none (required) | the git hosts (see above): the controller reads each one's address, token, bot login and triggers. Every host's token is kept out of logs and records |
| `FACTORY_MAX_TOKENS_PER_RUN`, `FACTORY_DAILY_TOKEN_LIMIT`, `FACTORY_REPO_DAILY_TOKEN_LIMIT`, `FACTORY_CONFIRM_OVER_TOKENS` | `0`, `0`, `0`, `0` (set by Compose) | token limits, as above; a value that is not `0` or a whole number from 10000 stops the controller at start with a message, so a typo cannot switch a limit off |
| `FACTORY_ALLOW_REPO_IMAGES` | `false` (set by Compose) | whether a repository's own image is mentioned and used, as above; the launcher enforces the same switch |
| `FACTORY_JANITOR`, `FACTORY_JANITOR_INTERVAL_HOURS`, `FACTORY_JANITOR_CHECKOUT_DAYS`, `FACTORY_JANITOR_WIP_DAYS`, `FACTORY_JANITOR_DELETE_WIP` | `true`, `24`, `2`, `30`, `false` (set by Compose) | the janitor, described under [Cleaning up](operations.md#cleaning-up); a value out of range stops the controller at start with a message |
| `FACTORY_PERMISSION_CACHE_SECONDS` | `300` (set by Compose) | how long a person's access on the host is kept (see above); a value out of range stops the controller at start |
| `FACTORY_GIT_AUTHOR_NAME`, `FACTORY_GIT_AUTHOR_EMAIL` | `AI SDLC Factory`, `factory-bot@ai-sdlc-factory.test` (set by Compose) | the author of the factory's commits (see above) |
| `FACTORY_DEFAULT_BUILD_ROUNDS`, `FACTORY_DEFAULT_CLARIFICATION_ROUNDS`, `FACTORY_DEFAULT_REVISIONS_PER_PULL_REQUEST` | `3`, `3`, `3` (set by Compose) | defaults for a project's `limits`; a value outside its range stops the controller at start with a message |
| `FACTORY_MODEL` | empty (set by Compose, controller and launcher) | the model for every role that names none (see above) |
| `FACTORY_WORKSPACE_ROOT` | none (required) | where checkouts live |
| `FACTORY_AGENTS_DIR` | `/etc/factory` (set by Compose) | the read-only agents folder, used only to name the model per role in run records |
| `TEMPORAL_UI_URL` | `http://localhost:8080` | base of the trace link in comments |

### alert-intake

The service that turns monitoring alerts into tickets is a tool of its own, outside the factory (its own settings, its own list of git hosts): see [its README](https://github.com/ai-sdlc-factory/ai-sdlc-factory-alert-intake#settings).

### sandbox-launcher

| Variable | Default | Meaning |
| --- | --- | --- |
| `SANDBOX_IMAGES` | seven images (`dotnet`, `python`, `node`, `dotnet-node`, `python-node`, `full`, `browser`), set in Compose | the allowlist of image aliases: `alias=image;alias=image`. **Add a language by adding a pair** (and a `sandboxes/<name>/Dockerfile`) |
| `SANDBOX_NETWORKS`, `SANDBOX_PROXIES` | set in Compose | which internal network and proxy address each egress profile uses; both must list the same profiles |
| `SANDBOX_EGRESS_MODE` | `enforce` (from `SANDBOX_EGRESS`) | `enforce`, `audit` or `open` |
| `SANDBOX_OPEN_NETWORK` | `ai-sdlc-factory_sandbox-open` | the ordinary network used in `open` mode |
| `SANDBOX_AUTH_VOLUME` | `ai-sdlc-factory_opencode-data` (set in Compose) | the volume holding the OpenCode login. Empty disables model runs |
| `FACTORY_ALLOW_REPO_IMAGES` | `false` (set by Compose) | `true` lets the launcher build the image a repository describes in `.factory/Dockerfile` (memory- and time-limited, at most 6 GB). Without it every such request is refused |
| `FACTORY_JANITOR_IMAGE_DAYS` | `60` (set by Compose) | repository images (built from `.factory/Dockerfile`) older than this many days and not used by a container are removed once a day, only while `FACTORY_ALLOW_REPO_IMAGES` is on; 0 keeps them. They are rebuilt when a repository needs them again |
| `SANDBOX_AUTH_READONLY` | `false` (set by Compose; change it in `deploy/.env`) | `true` forbids an agent from changing the login, at the cost of a login that stops working when its token expires |
| `FACTORY_GLOBAL_CONFIG` | from `.env` | the agents folder mounted at `/etc/factory` |
| `SANDBOX_UID`, `SANDBOX_GID` | `1000` | the user the sandbox runs as (from `FACTORY_UID`/`FACTORY_GID`); must not be root |
| `SANDBOX_MEMORY_BYTES` | 2 GiB, `2147483648` (set by Compose; change it in `deploy/.env`) | memory limit per sandbox (swap equal, so no swapping) |
| `SANDBOX_CPUS` | `2` (set by Compose; change it in `deploy/.env`) | CPU limit per sandbox |
| `SANDBOX_PIDS_LIMIT` | `1024` (set by Compose; change it in `deploy/.env`) | process and thread limit per sandbox (a browser, an application and the agent together need several hundred; too low and a QA run dies with exit code 134) |
| `SANDBOX_MAX_RUN_SECONDS` | `1800` (set by Compose; change it in `deploy/.env`) | the longest a single run may take; a request's own timeout is capped by it |
| `SANDBOX_MAX_CONCURRENT` | `2` (set by Compose; change it in `deploy/.env`) | sandboxes running at once; others wait |
| `SANDBOX_TASK_QUEUE` | `sandbox` (fixed: the controller uses the same name; Compose does not pass it) | the Temporal queue the launcher polls |
| `DOCKER_SOCKET` | `/var/run/docker.sock` (fixed: Compose mounts the socket there; not a setting to change) | where the launcher reaches Docker |

What the launcher fixes for **every** sandbox, whatever a request says: non-root user, read-only root filesystem with a 1 GB `/tmp`, all capabilities dropped, `no-new-privileges`, no
privileged mode, no devices, an init process, the memory, CPU and process limits above, and no mounts except the workspace, the agents folder (read-only) and, for model runs, the
login volume. Details: [launcher guide](../src/sandbox-launcher/README.md).

### egress-proxy

| Variable | Default | Meaning |
| --- | --- | --- |
| `EGRESS_PROFILES_FILE` | `/etc/egress/profiles.json` (mounted from `EGRESS_PROFILES`) | the allowlist file; the proxy refuses to start if it cannot parse it |
| `EGRESS_LISTEN_MAP` | set in Compose | which profile each listen address belongs to |
| `EGRESS_MODE` | `enforce` (from `SANDBOX_EGRESS`) | `enforce`, `audit`, `open` (open bypasses the proxy entirely) |
| `EGRESS_PORT` | `3128` (fixed: the sandboxes' proxy addresses name it; not a setting to change) | the proxy's port |
| `EGRESS_DENIED_LOG` | `/var/log/egress/denied.jsonl` (set by Compose) | a file every refusal is also written to, one JSON line each, at most 10 MB; the controller's [weekly report](operations.md#the-weekly-report-what-repeats) reads it. Empty = no file (refusals are still on the console) |

The allowlist format, adding a host, and what the protection does and does not cover are in the [egress guide](../src/egress-proxy/ALLOWLIST.md).

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
| `model` | optional: the model for this role. A role that leaves it out uses `FACTORY_MODEL`, else the default in `opencode.json` (`openai/gpt-6.1-sol`). None of the shipped roles names one |
| `variant` | OpenCode's reasoning preset for the model, for example `low` or `high`; which values exist depends on the model |
| `reasoningEffort`, `temperature` | passed through to the provider; accepted by OpenCode in a role file (checked with `opencode debug agent <role>`) |
| `permission` | what the role may do. The baseline in `opencode.json` is deny-all except reading (`read`, `glob`, `grep`); a role adds `edit: allow` and/or `bash: allow` |

Which roles can do what:

| Role | Used in | Reads | Edits files | Runs commands | Network profile |
| --- | --- | --- | --- | --- | --- |
| `analyst`, `coverage`, `architect` | feature | yes | no | no | `model` |
| `developer` | feature, bug | yes | yes | yes | `developer` (model + package registries) |
| `reviewer`, `security`, `ux`, `acceptance` | feature, bug (`acceptance`: feature; `ux` also reads the evidence of a `/qa` run) | yes (given the diff) | no | no | `model` |
| `bug-analyst`, `diagnostician` | bug | yes | no | no | `model` |
| `reproducer` | bug | yes | yes (tests only, checked) | yes | `developer` |
| `pr-resolver` | pull request revision | yes | yes (checked like the developer's changes) | yes | `developer` |
| `merge-resolver` | pull request update (`/update`) | yes (and git history) | yes (checked like the developer's changes; no protected files, no conflict marker left) | yes | `developer` |
| `docs-writer` | documentation run (`docs` label) | yes | yes (only `AGENTS.md`, `README.md`, `CONTRIBUTING.md` and Markdown under `docs/`, checked) | no | `model` |
| `asker` | question (`/ask`) | yes | no | no | `model` |
| `configurer` | setup (`/setup`) | yes | no | no | `model` |
| `improver` | the weekly improvement run | yes (the factory's own repository) | no | no | `model` |
| `incident-analyst`, `mitigator` | incident | yes | no | no | `model` |
| `planner` | plan | yes | no | no | `model` |
| `investigator` | incident | yes (and git history) | no | yes (read-only workspace) | `model` |
| `postmortem-writer` | incident | yes | yes (one new file, checked) | no | `model` |
| `e2e-tester` | qa (`/qa`) | yes | yes (files under the project's `verify.tests` folder only, checked) | yes | `developer` |

The project's checks run with the `packages` profile (registries only, no model), and the factory's own small helper commands (does this file exist, a commit exists, a file hash) run with no network.
Roles are chosen in code, never by a repository or an agent.

Choosing a model: check it works with your login first (`opencode models openai` from the [login guide](opencode-login.md); a refused model makes that role fail with an unclear
"exited with code" message). Re-check the role after a change: prompts and the strict JSON answers were tuned on one model. Reasoning tokens and cost per role appear in each
[run record](workflows.md#the-run-record), so you can compare settings. See the [agents guide](../agents/README.md#choosing-the-model).

## Fixed limits (not configurable)

Build rounds (3), question rounds, retry counts, the 7-day wait, file and size limits and agent timeouts are constants in the controller code. The table is in
[Workflows](workflows.md#limits-and-timeouts).
