# Dev Foundry

Local infrastructure for an AI development factory, running with Docker Compose
inside WSL. Forgejo provides source control in place of GitLab.

## Repository layout

Deployment files live in `infra/`:

```text
dev-foundry/
??? README.md
??? infra/
    ??? compose.yaml
    ??? .env.example
    ??? pgadmin/
    ??? postgres/
    ??? temporal/
```

Future application code can live in `services/`, agent definitions in `agents/`,
workflow code in `workflows/`, and factory settings in `config/`.

## Services

| Service | Version | Local address |
| --- | --- | --- |
| Forgejo | 17.0.1 | http://localhost:3000 |
| PostgreSQL | 17.11 | localhost:5432 |
| pgAdmin | 9.18.0 | http://localhost:5050 |
| Mailpit | 1.32.0 | http://localhost:8025 (inbox), localhost:1025 (SMTP) |
| Temporal server | 1.32.1 | localhost:7233 (gRPC) |
| Temporal UI | 2.57.0 | http://localhost:8080 |

All published ports bind to `127.0.0.1`. This stack is for local development;
Temporal has no authentication configured. Docker Desktop must be running with
WSL integration enabled for Ubuntu. Use Docker Compose v2 or newer.

## Start from WSL

```bash
cd /home/valters/dev-foundry
cp infra/.env.example infra/.env
chmod 600 infra/.env
cd infra
```

Edit `infra/.env`, set its four passwords to different values, and update the pgAdmin
login email if desired. Generate a password with `openssl rand -hex 24`.
The `infra/.env` file is ignored by Git. Empty passwords prevent Compose from starting.
Run the following commands from the `infra/` directory:

```bash
docker compose config --quiet
docker compose up -d
docker compose ps -a
```

Alternatively, run Compose from the repository root with explicit paths:

```bash
docker compose --env-file infra/.env -f infra/compose.yaml up -d
```

The first start downloads the images and initializes the databases. The
`temporal-schema` and `temporal-namespace` services should finish with exit code
`0`; the other six services remain running.

### Shared environment settings

`infra/.env` is the shared configuration for this local deployment. Compose reads
it for variable substitution and passes only explicitly mapped values to each
container. Do not mount the entire file into application containers.

`TEMPORAL_ADDRESS` is shared by the Temporal namespace setup and UI services.
`SMTP_HOST` and `SMTP_PORT` configure Forgejo's outgoing mail. Future services
in this Compose project can reference those same variables through their
`environment:` mappings. These values use container endpoints (`temporal:7233`
and `mailpit:1025`); applications running directly in WSL use the corresponding
`localhost` addresses and published host ports instead.

Keep deployment-wide settings here. An application that runs independently may
have its own local `.env`; give it its own database credentials rather than
reusing another application's account.

### Forgejo setup

Open http://localhost:3000 and finish the initial installation. Database settings
are supplied by Compose: host `postgres:5432`, database/user `forgejo`, password
`FORGEJO_DB_PASSWORD` from `.env`. Create your administrator account in the
installation form. Public account registration is disabled.

Git over SSH is available on port `2222`, for example:
`ssh://git@localhost:2222/owner/repository.git`.
Forgejo Actions runners will be added separately when CI is needed.

### Local email capture

Forgejo sends email through Mailpit at `mailpit:1025`, without SMTP
authentication or TLS on the Compose network. Email notifications are enabled.
Password resets and issue/pull request notifications are captured locally rather
than delivered to real recipients. Mailpit has no outbound relay configured.

Open http://localhost:8025 to read captured messages; no login is required.
Messages persist in the `mailpit-data` volume, with the newest 5,000 retained.
To test delivery, sign in to Forgejo as an administrator and open **Site
Administration > Configuration > SMTP Mailer Configuration** to send a test email.
The message should appear in Mailpit.

Other services can use `mailpit:1025` inside this Compose project or
`localhost:1025` from WSL. The host ports can be overridden with
`MAILPIT_UI_PORT` and `MAILPIT_SMTP_PORT` in `infra/.env`. Forgejo uses the
shared `SMTP_HOST` and `SMTP_PORT` settings, which default to `mailpit:1025`.
Host port overrides do not change these container connection settings.

After adding this service to an already running stack, apply the configuration
with `docker compose up -d`. Compose will start Mailpit and recreate Forgejo
with the mail settings.

### PostgreSQL and pgAdmin

Log in at http://localhost:5050 using `PGADMIN_DEFAULT_EMAIL` and
`PGADMIN_DEFAULT_PASSWORD`. The PostgreSQL server is preconfigured; enter
`POSTGRES_PASSWORD` when connecting. This is the database administrator password,
which is separate from the pgAdmin login password.

The PostgreSQL instance contains three application databases:

| Database | Owner |
| --- | --- |
| `forgejo` | `forgejo` |
| `temporal` | `temporal` |
| `temporal_visibility` | `temporal` |

Application accounts have no superuser or database-creation privileges and cannot
connect to the other application's databases. Future factory applications should
receive their own database and role.

From WSL, database clients connect to `localhost:5432`; containers in this Compose
project use `postgres:5432`.

### Temporal

Temporal uses PostgreSQL for both workflow persistence and visibility. The setup
services initialize its schemas and create the `default` namespace with seven-day
retention for closed workflows. These setup operations are safe to rerun.

Workers running in WSL should connect to `localhost:7233`, namespace `default`.
Containers in this Compose project should connect to `temporal:7233`. Open
http://localhost:8080 to inspect workflows. No application workers are included yet.

## Everyday commands

Run these commands from `infra/`:

```bash
docker compose ps -a                 # Include completed setup services
docker compose logs -f              # Follow logs; Ctrl+C leaves services running
docker compose stop                 # Stop services, keep containers and data
docker compose up -d                # Start again
docker compose down                 # Remove containers/network, keep data volumes
```

Named volumes preserve PostgreSQL data, Forgejo repositories/configuration,
pgAdmin settings, and Mailpit messages. `docker compose down --volumes` deletes
those volumes and all their data; use it only for an intentional clean reset.

Host ports can be changed in `infra/.env` before starting. Container ports and the
pgAdmin server definition remain unchanged. Forgejo's initial installation form
should show the matching localhost URL and SSH port.

PostgreSQL initialization runs only when its data volume is empty. Changing a
password in `infra/.env` does **not** rotate an existing database role's password;
change it in PostgreSQL as well before recreating dependent containers. Likewise,
pgAdmin's initial login values apply only when its volume is first initialized.

Image versions are pinned in `infra/compose.yaml`. Before upgrades, back up the data
and review each project's upgrade instructions, particularly PostgreSQL major
versions and Temporal schema migrations.

## References

- [Forgejo Docker installation](https://forgejo.org/docs/latest/admin/installation/docker/)
- [Forgejo email setup](https://forgejo.org/docs/latest/admin/setup/email/)
- [Mailpit Docker deployment](https://mailpit.axllent.org/docs/install/docker/)
- [PostgreSQL image](https://hub.docker.com/_/postgres)
- [pgAdmin container deployment](https://www.pgadmin.org/docs/pgadmin4/latest/container_deployment.html)
- [Temporal self-hosted deployment](https://docs.temporal.io/self-hosted-guide/deployment)
- [Temporal Compose examples](https://github.com/temporalio/samples-server/tree/main/compose)
