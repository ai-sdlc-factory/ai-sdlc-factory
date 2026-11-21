# Infrastructure

[Documentation index](../docs/README.md) | [Getting started](../docs/getting-started.md) | [Configuration reference](../docs/configuration.md)

The local Docker Compose stack, for WSL or Linux. Service definitions and pinned image versions are in [compose.yaml](compose.yaml); the settings are in [.env.example](.env.example) and
explained in the [configuration reference](../docs/configuration.md). **To set it up for the first time, follow [Getting started](../docs/getting-started.md)**; this page describes what the stack contains.

## Start

Requires Docker Desktop with Ubuntu WSL integration (or Docker Engine) and Docker Compose v2. From the repository root:

```bash
cd infra
cp -n .env.example .env  # keeps an existing .env
chmod 600 .env
# fill in the passwords, the webhook secret and the FACTORY_* values (see Getting started)
docker compose up -d --build
docker compose ps -a
```

The api, controller, sandbox launcher and egress proxy build separate images. The build context is the repository root, so every image can use the shared build settings and contracts library. The first start initializes
the databases: `temporal-schema` and `temporal-namespace` should exit with code `0`; the other services keep running. Optional profiles are excluded from a normal start: `images` (the sandbox images),
`opencode-tools` (the login helper) and `test` (the [integration tests](../tests/integration/README.md)).

## Services

| Service | Local address | Purpose |
| --- | --- | --- |
| api | http://localhost:8000 ([OpenAPI](http://localhost:8000/openapi/v1.json), `/health/live`, `/health/ready`) | [webhook intake](../services/api/README.md) |
| Temporal | `localhost:7233` (gRPC) | workflow engine |
| Temporal UI | http://localhost:8080 | every workflow, with each prompt, answer and check output |
| controller | task queue `factory-events`; no HTTP port | [the workflows](../services/controller/README.md) |
| sandbox-launcher | task queue `sandbox`; no HTTP port | [starts the agent containers](../services/sandbox-launcher/README.md) (the only service with the Docker socket) |
| egress-proxy | internal only | [the sandboxes' only way out](../services/egress-proxy/README.md) |
| PostgreSQL | `localhost:5432` | Temporal data |
| pgAdmin | http://localhost:5050 | database browser (development) |

Compose isolates services on separate networks: `data` (PostgreSQL and its clients), `control` (Temporal, the api, the controller and the launcher), `hooks` (a git host to the api) and `forge` (the controller to a git host; the Forgejo of [`../companions/forgejo`](../companions/forgejo/README.md) joins both)
and `agents` (the OpenCode login helper only, which has internet access for the browser login). The disposable agent containers run on three **internal** networks (`sandbox-model`, `sandbox-packages`, `sandbox-developer`)
that have no route out and no external DNS; the only thing on them besides the sandboxes is the egress proxy, which lets each profile reach just the hosts listed in [`services/egress-proxy/profiles.json`](../services/egress-proxy/ALLOWLIST.md).
The sandboxes cannot reach PostgreSQL, a git host, Temporal or the api. Their networks use `10.250.x.0/24`; set `SANDBOX_NET_BASE` in `.env` if that clashes with a network you use. `SANDBOX_EGRESS` (`enforce`, `audit`, `open`) and
`EGRESS_PROFILES` (your own allowlist file) change how strict this is; see the [egress guide](../services/egress-proxy/ALLOWLIST.md#modes-and-your-own-rules). See what a sandbox tried to reach with `docker compose logs egress-proxy`.

Host ports can be changed in `.env`. All published ports bind to localhost. This is a local development stack; Temporal has no authentication.

## First use

**A git host:** the factory serves the hosts of `FACTORY_HOSTS` and starts none itself. To try it on a Forgejo of your own, run the one in [`../companions/forgejo`](../companions/forgejo/README.md) (a project of its own), then create the [factory bot account](../docs/getting-started.md#4-set-up-a-git-host-and-the-factorys-bot-account) and [connect each repository](../docs/project-setup.md). For GitHub, see [Several git hosts](../docs/operations.md#several-git-hosts).

**pgAdmin:** log in with `PGADMIN_DEFAULT_EMAIL` and `PGADMIN_DEFAULT_PASSWORD` from `.env`. Open **Dev Foundry PostgreSQL** and connect as `postgres` using `POSTGRES_PASSWORD`.

**Temporal:** the setup service creates `TEMPORAL_NAMESPACE` (default `default`) with `TEMPORAL_RETENTION` (default `7d`) for closed workflows. Retention is applied only when the namespace is created.

## Connecting applications

| Service | From WSL | From the same Compose network |
| --- | --- | --- |
| PostgreSQL | `localhost:5432` | `postgres:5432` |
| Temporal | `localhost:7233` | `temporal:7233` |

The addresses above use the default host ports. Compose passes each container only its explicitly mapped variables from `.env`. `TEMPORAL_ADDRESS` configures an internal connection; host port
overrides do not change container ports. Give future applications their own database accounts.

## Workspaces and sandboxes

Tickets get a private git checkout on the host and run agents in disposable containers. The relevant `.env` settings are `FACTORY_WORKSPACE_ROOT` (create it first with `mkdir -p`; the same path is used inside containers),
`FACTORY_UID`/`FACTORY_GID`, `DOCKER_GID` and `FACTORY_GLOBAL_CONFIG` (the absolute path of the [`agents/`](../agents/README.md) folder, mounted read-only into every sandbox and into the controller). They are explained in
the [configuration reference](../docs/configuration.md#infraenv).

Sandboxes that call the model share the OpenCode login stored in the `opencode-data` volume (see [the launcher guide](../services/sandbox-launcher/README.md#model-access-and-the-opencode-login) and the
[login guide](../docs/opencode-login.md)). The one-shot `opencode-data-init` service gives that volume to `FACTORY_UID`; it starts by itself before the login helper and needs no action from you.

Build the sandbox images once (`dotnet`, `python` and `node`; plus `dotnet-node`, `python-node` and `full` for projects that need two toolchains, and `browser` for the UI suite of a `/qa` run), then the launcher and controller are already running:

```bash
docker compose --profile images build sandbox-image-dotnet sandbox-image-python sandbox-image-node
```

Checkouts are removed when a ticket's workflow ends; folders of runs you terminated by hand remain (see [Operations](../docs/operations.md#cleaning-up)).

## Factory bot account

The controller comments on tickets, pushes branches and opens pull requests as a dedicated user of each git host, so the factory never uses a personal account. How to create it on the Forgejo of [`../companions/forgejo`](../companions/forgejo/README.md#the-factory-bot-account), or on GitHub ([Several git hosts](../docs/operations.md#several-git-hosts)); put its token in the `"token"` of that host in `FACTORY_HOSTS` in `.env`, then recreate the controller (`docker compose up -d --force-recreate controller`). The bot must be a collaborator with write access on each repository it serves ([project setup](../docs/project-setup.md#1-give-the-bot-access)).

## OpenCode login

The agents need a ChatGPT login, created once with `docker compose --profile opencode-tools run --rm --service-ports opencode-login`. See the [OpenCode login guide](../docs/opencode-login.md). The helper runs only
when explicitly targeted; the `opencode-data` volume stores the login.

## Everyday commands

Run from `infra/`:

```bash
docker compose ps -a      # Status, including completed setup services
docker compose logs -f    # Logs; append a service name to filter
docker compose up -d      # Start services or apply configuration changes
docker compose stop      # Stop services
docker compose down      # Remove containers/network; keep data
```

Data persists in named volumes (`postgres-data`, `pgadmin-data`, `opencode-data`, `egress-log`). `docker compose down --volumes` deletes all stack data, including the databases and the OpenCode login. Database initialization and pgAdmin login setup run only on empty volumes; changing passwords in `.env` does not update the existing database accounts or the existing pgAdmin login.
