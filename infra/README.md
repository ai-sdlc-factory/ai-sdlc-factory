# Infrastructure

[Project overview](../README.md) | [Controller API](../services/factory-controller-api/README.md) | [Worker](../services/factory-controller-worker/README.md)

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

The API and worker build separate images from their own service directories.
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
| Controller API | http://localhost:8000/docs |
| Controller worker | Temporal task queue; no HTTP port |

Compose isolates services on four networks: `data` (PostgreSQL and its clients),
`control` (Temporal and the controller services), `hooks` (Forgejo to the controller
API) and `agents` (Temporal and the OpenCode worker only). The OpenCode worker
cannot reach PostgreSQL, Forgejo or the controller API. Outbound internet access is
not restricted.

Host ports can be changed in `.env`. All published ports bind to localhost.
This is a local development stack; Temporal and Mailpit have no authentication.

## First use

**Forgejo:** finish the installation at http://localhost:3000 and create an
administrator account. Database settings are supplied by Compose. Public
registration is disabled. Follow the [API guide](../services/factory-controller-api/README.md#connect-forgejo)
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

## Optional OpenCode demo

The [OpenCode guide](../services/opencode-worker/README.md) covers ChatGPT browser
login and a manual Temporal task. Enable the worker with `--profile opencode`.
Login and demo helpers run only when explicitly targeted. The `opencode-data` volume stores the login.

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
