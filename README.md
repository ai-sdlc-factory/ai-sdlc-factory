# Dev Foundry

Local infrastructure for an AI development factory. Deployment files live in
`infra/`; image versions are pinned in `infra/compose.yaml`.

## Start

Requires Docker Desktop with Ubuntu WSL integration and Docker Compose.
From the repository root:

```bash
cd infra
cp -n .env.example .env  # Keep an existing .env
chmod 600 .env
```

Set the four passwords in `.env` and choose a pgAdmin login email.
Generate passwords with `openssl rand -hex 24`. The `.env` file is ignored by Git.

```bash
docker compose up -d
docker compose ps -a
```

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

Host ports can be changed in `infra/.env`. All published ports bind to localhost.
This is a local development stack; Temporal and Mailpit have no authentication.

## First use

**Forgejo:** finish the installation at http://localhost:3000 and create an
administrator account. Database settings are supplied by Compose. Public
registration is disabled.

**pgAdmin:** log in with `PGADMIN_DEFAULT_EMAIL` and `PGADMIN_DEFAULT_PASSWORD`
from `infra/.env`. Open **Dev Foundry PostgreSQL** and connect as `postgres`
using `POSTGRES_PASSWORD`.

**Mailpit:** Forgejo emails appear at http://localhost:8025 and are not delivered
externally. No login is needed. Messages persist across restarts; the newest
5,000 are retained. Send a test email from Forgejo's **Site Administration >
Configuration > SMTP Mailer Configuration**.

**Temporal:** use namespace `default`. Closed workflows have seven-day retention.

## Connecting applications

| Service | From WSL | From the same Compose network |
| --- | --- | --- |
| PostgreSQL | `localhost:5432` | `postgres:5432` |
| Temporal | `localhost:7233` | `temporal:7233` |
| SMTP | `localhost:1025` | `mailpit:1025` (no authentication or TLS) |

`infra/.env` holds shared deployment settings. Compose passes each container only
its explicitly mapped variables. `TEMPORAL_ADDRESS`, `SMTP_HOST`, and `SMTP_PORT`
configure internal connections; host port overrides do not change container ports.
Give future applications their own database accounts.

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
stack data, including repositories, databases, and captured emails.

Database initialization and pgAdmin login setup run only on empty volumes.
Changing passwords in `.env` does not update existing database accounts or
the existing pgAdmin login.
