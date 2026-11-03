# Infrastructure

[Project overview](../README.md) | [Controller API](../services/api/README.md) | [Worker](../services/controller/README.md)

Local Compose stack for WSL. Service definitions and image versions live in
[compose.yaml](compose.yaml); shared settings are listed in [.env.example](.env.example).

## Start

Requires Docker Desktop with Ubuntu WSL integration and Docker Compose.
From the repository root:

```bash
cd infra
cp -n .env.example .env  # Keep an existing .env
chmod 600 .env
```

Set the four passwords and `FORGEJO_WEBHOOK_SECRET` (at least 32 characters)
in `.env`, and choose a pgAdmin login email. Generate values with
`openssl rand -hex 32`. The `.env` file is ignored by Git.

Run Compose commands from `infra/`:

```bash
docker compose up -d --build
docker compose ps -a
```

The API, controller and sandbox launcher build separate images. The build context is the
repository root, so every image can use the shared build settings and contracts library.
Full pipeline tests are available through the optional
[test profile](../tests/integration/README.md).

The first start initializes the databases. `temporal-schema` and
`temporal-namespace` should exit with code `0`; the other services keep running.

## Services

| Service | Local address |
| --- | --- |
| Forgejo | http://localhost:3000 (SSH: port `2222`) |
| PostgreSQL | `localhost:5432` |
| pgAdmin | http://localhost:5050 |
| Mailpit inbox | http://localhost:8025 (SMTP: port `1025`) |
| Temporal | `localhost:7233` (gRPC) |
| Temporal UI | http://localhost:8080 |
| Controller API | http://localhost:8000/openapi/v1.json (OpenAPI document) |
| Controller worker | Temporal task queue; no HTTP port |

Compose isolates services on separate networks: `data` (PostgreSQL and its clients),
`control` (Temporal and the controller services), `hooks` (Forgejo to the controller API),
`forge` (Forgejo and the controller) and `agents` (the OpenCode login helper only, which has internet
access for the browser login). The disposable agent containers run on three **internal** networks
(`sandbox-model`, `sandbox-packages`, `sandbox-developer`) that have no route out and no external DNS;
the only thing on them besides the sandboxes is the egress proxy, which lets each profile reach just the
hosts listed in [`egress/profiles.json`](../egress/README.md). The sandboxes cannot reach PostgreSQL,
Forgejo, Temporal or the controller API. Their networks use `10.250.x.0/24`; set `SANDBOX_NET_BASE` in
`.env` if that clashes with a network you use. `SANDBOX_EGRESS` (`enforce`, `audit`, `open`) and
`EGRESS_PROFILES` (your own allowlist file) change how strict this is; see the
[egress guide](../egress/README.md#modes-and-your-own-rules). See what a sandbox tried to reach with
`docker compose logs egress-proxy`.

Host ports can be changed in `.env`. All published ports bind to localhost.
This is a local development stack; Temporal and Mailpit have no authentication.

## First use

**Forgejo:** finish the installation at http://localhost:3000 and create an
administrator account. Database settings are supplied by Compose. Public
registration is disabled. Follow the [API guide](../services/api/README.md#connect-forgejo)
to configure event delivery.

**pgAdmin:** log in with `PGADMIN_DEFAULT_EMAIL` and `PGADMIN_DEFAULT_PASSWORD`
from `.env`. Open **Dev Foundry PostgreSQL** and connect as `postgres`
using `POSTGRES_PASSWORD`.

**Mailpit:** Forgejo emails appear at http://localhost:8025 and are not delivered
externally. No login is needed. Messages persist across restarts; the newest
5,000 are retained. Send a test email from Forgejo's **Site Administration >
Configuration > SMTP Mailer Configuration**.

**Temporal:** the setup service creates `TEMPORAL_NAMESPACE` (default `default`)
with `TEMPORAL_RETENTION` (default `7d`) for closed workflows. Retention is applied
only when the namespace is created.

## Connecting applications

| Service | From WSL | From the same Compose network |
| --- | --- | --- |
| PostgreSQL | `localhost:5432` | `postgres:5432` |
| Temporal | `localhost:7233` | `temporal:7233` |
| SMTP | `localhost:1025` | `mailpit:1025` (no authentication or TLS) |

The addresses above use the default host ports. Compose passes each container
only its explicitly mapped variables from `.env`. `TEMPORAL_ADDRESS`, `SMTP_HOST`,
and `SMTP_PORT` configure internal connections; host port overrides do not change
container ports. Give future applications their own database accounts.

## Workspaces and sandboxes

Feature tickets get a private git checkout on the host and run agents in disposable containers.
Set these in `.env` (see `.env.example`):

- `FACTORY_WORKSPACE_ROOT`: an absolute host path for the checkouts. **Create it yourself first**
  (`mkdir -p`); Docker would otherwise create it as root. The same path is used inside containers.
- `FACTORY_UID` / `FACTORY_GID`: your user (`id -u`, `id -g`). The controller and sandboxes run as
  this user, so you can read, change and delete the checkouts.
- `DOCKER_GID`: the group of the Docker socket (`stat -c %g /var/run/docker.sock`), so the sandbox
  launcher does not need root.
- `FACTORY_GLOBAL_CONFIG`: the absolute path of this repository's [`agents/`](../agents/README.md)
  folder (agent definitions and OpenCode settings), mounted read-only into every sandbox.

Sandboxes that call the model share the OpenCode login stored in the `opencode-data` volume (see
[the launcher guide](../services/sandbox-launcher/README.md#model-access-and-the-opencode-login)).
The one-shot `opencode-data-init` service gives that volume to `FACTORY_UID`; it starts by itself
before the login and demo services and needs no action from you.

Build the sandbox images once (`dotnet`, `python` and `node`), then start the launcher:

```bash
docker compose --profile images build sandbox-image-dotnet sandbox-image-python sandbox-image-node
docker compose up -d --build sandbox-launcher controller
```

Checkouts are removed when a ticket's workflow ends. Details are in the
[sandbox launcher guide](../services/sandbox-launcher/README.md).

## Factory bot account

The controller comments on tickets as a dedicated Forgejo user, so the factory never uses a personal
account. Create it once (the password is random and not needed afterwards):

```bash
docker compose exec -u 1000 forgejo forgejo admin user create \
  --username factory-bot --email factory-bot@dev-foundry.test \
  --random-password --must-change-password=false
docker compose exec -u 1000 forgejo forgejo admin user generate-access-token \
  --username factory-bot --token-name controller --scopes "write:issue,read:repository" --raw
```

Put the printed token in `.env` as `FORGEJO_BOT_TOKEN`, then recreate the controller
(`docker compose up -d --force-recreate controller`). The controller reaches Forgejo over the
dedicated `forge` network, so recreate Forgejo once after pulling this change as well.

The token created above can only comment. The build stage also pushes branches and opens pull requests,
so create it with `--scopes "write:issue,write:repository"`; the bot account must be a collaborator with
write access on the repository (or own it).

For each repository the factory should serve:

1. Add `factory-bot` as a collaborator (write access) so it can comment.
2. Add a webhook to `http://api:8000/webhooks/forgejo` (JSON, secret from `FORGEJO_WEBHOOK_SECRET`)
   for the Issues and Issue Comment events.
3. Add `.factory/project.json` (sandbox image and checks; see the
   [controller guide](../services/controller/README.md#project-configuration)) on the default branch.
4. Create the labels `feature`, `bug` and `incident` (and optionally `quick`, `full`, `needs-architecture`). A ticket labeled
   `feature` starts a feature workflow, `bug` a bug workflow, `incident` an advice-only incident workflow.

## OpenCode login

The agents need a ChatGPT login, created once with `docker compose --profile opencode-tools run --rm
--service-ports opencode-login`. See the [OpenCode login guide](../docs/opencode-login.md). The helper
runs only when explicitly targeted; the `opencode-data` volume stores the login.

## Everyday commands

Run from `infra/`:

```bash
docker compose ps -a      # Status, including completed setup services
docker compose logs -f    # Logs; append a service name to filter
docker compose up -d      # Start services or apply configuration changes
docker compose stop      # Stop services
docker compose down      # Remove containers/network; keep data
```

Data persists in named volumes. `docker compose down --volumes` deletes all
stack data, including repositories, databases, captured emails, and the OpenCode login.

Database initialization and pgAdmin login setup run only on empty volumes.
Changing passwords in `.env` does not update existing database accounts or
the existing pgAdmin login.
