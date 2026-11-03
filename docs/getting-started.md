# Getting started: setting up the factory

[Documentation index](README.md) | next: [Setting up a project](project-setup.md)

This guide takes you from a clean machine to a running factory. It is done **once**. Afterwards, each repository the factory should
serve needs a [short setup](project-setup.md).

## What you need

| Requirement | Notes |
| --- | --- |
| Windows with **WSL 2** (Ubuntu) and **Docker Desktop** with WSL integration enabled, or any Linux with Docker Engine and Compose v2 | The stack is built and tested on WSL. Docker Compose runs everything. |
| About 10 GB of disk and 8 GB of RAM for Docker | The sandboxes are limited to 2 GB and 2 CPUs each, and 2 run at once. |
| A **ChatGPT Plus/Pro account** | The agents use OpenCode with a ChatGPT sign-in; there is no OpenAI API key. Usage counts against your plan. |
| Internet access for Docker image builds | The agents' own network access is restricted separately ([egress](../egress/README.md)). |

## 1. Get the code and configure the environment

```bash
git clone <this repository> dev-foundry
cd dev-foundry/infra
cp -n .env.example .env      # keeps an existing .env
chmod 600 .env
```

Open `infra/.env` and set the values below. Everything else has a working default; the full list is in [Configuration](configuration.md).

| Setting | Required | What to put |
| --- | --- | --- |
| `POSTGRES_PASSWORD`, `FORGEJO_DB_PASSWORD`, `TEMPORAL_DB_PASSWORD`, `PGADMIN_DEFAULT_PASSWORD` | **yes** | four different passwords: `openssl rand -hex 24` |
| `PGADMIN_DEFAULT_EMAIL` | yes | any login email for pgAdmin |
| `FORGEJO_WEBHOOK_SECRET` | **yes** | at least 32 characters: `openssl rand -hex 32` (you paste it into each repository's webhook later) |
| `FACTORY_WORKSPACE_ROOT` | **yes** | an absolute host path for the checkouts. **Create it first**: `mkdir -p /var/tmp/dev-foundry-workspaces` (Docker would create it as root otherwise) |
| `FACTORY_UID`, `FACTORY_GID` | **yes** | your user: `id -u`, `id -g` |
| `DOCKER_GID` | **yes** | the group of the Docker socket: `stat -c %g /var/run/docker.sock` |
| `FACTORY_GLOBAL_CONFIG` | **yes, in practice** | the absolute path of this repository's `agents/` folder, for example `/home/you/dev-foundry/agents`. Without it the sandboxes run without the role files and the agents cannot work |
| `FORGEJO_BOT_TOKEN` | **yes** (step 4) | leave empty for now |
| `SANDBOX_EGRESS` | no | `enforce` (default), `audit` or `open`: how strictly the sandboxes' internet access is limited |

The `.env` file is ignored by Git. Never commit it.

## 2. Start the stack

```bash
docker compose up -d --build
docker compose ps -a
```

The first start initialises the databases. `temporal-schema` and `temporal-namespace` should exit with code `0`; everything else keeps running.
Check the stack:

| Check | Expected |
| --- | --- |
| `docker compose ps -a` | all services `running` or `healthy`; the two Temporal setup services `exited (0)` |
| http://localhost:8000/health/ready | `200`: the api can reach Temporal |
| http://localhost:8080 | the Temporal UI |
| http://localhost:3000 | Forgejo's installer or login page |

## 3. Build the sandbox images

The agents run in images for each language your projects use. Build them once (they are not part of `up`):

```bash
docker compose --profile images build sandbox-image-dotnet sandbox-image-python sandbox-image-node
```

Build only the ones you need; a project that asks for an image that does not exist fails its first agent run with a clear message.
Adding another language is described in the [launcher guide](../services/sandbox-launcher/README.md#images).

## 4. Set up Forgejo and the factory's bot account

1. Open http://localhost:3000 and finish the installation. Create an **administrator** account. Database settings are supplied by Compose and public
   registration is disabled.
2. Create the **bot account** the factory uses to comment, push branches and open pull requests, so it never uses a personal account. The password is random and not needed afterwards:

   ```bash
   docker compose exec -u 1000 forgejo forgejo admin user create \
     --username factory-bot --email factory-bot@dev-foundry.test \
     --random-password --must-change-password=false
   docker compose exec -u 1000 forgejo forgejo admin user generate-access-token \
     --username factory-bot --token-name controller --scopes "write:issue,write:repository" --raw
   ```

3. Put the printed token into `infra/.env` as `FORGEJO_BOT_TOKEN` and recreate the controller:

   ```bash
   docker compose up -d --force-recreate controller
   ```

   Without the token every feature, bug and incident run fails at its first comment; all other events still work.

## 5. Sign the agents in

The agents use a ChatGPT login stored once in a Docker volume:

```bash
docker compose --profile opencode-tools run --rm --service-ports opencode-login
```

Open the printed URL in your browser, sign in, and keep the terminal open until it completes. Details, the device-login alternative and
troubleshooting are in the [login guide](opencode-login.md). Sign-in is needed again only if the volume is deleted
(`docker compose down -v` removes it).

## 6. Check that everything works

```bash
docker compose --profile opencode-tools run --rm --no-deps opencode-login opencode auth list
docker compose logs --tail 20 controller sandbox-launcher
```

The login should be listed, and both services should be waiting for work without errors. Optionally run the automated checks:

```bash
# unit tests of each service (from the repository root)
docker build --target test -f services/controller/Dockerfile -t dev-foundry/controller-test . && docker run --rm dev-foundry/controller-test
# full pipeline test against the running stack (from infra/)
docker compose --profile test run --build --rm --no-deps integration-tests
```

## 7. Connect your first repository

Follow [Setting up a project](project-setup.md). It takes five minutes: bot access, a webhook, labels and a small `.factory/project.json`. Then create a
ticket, add the `feature` label, and watch the comments appear.

## Everyday commands

Run from `infra/`:

```bash
docker compose ps -a                         # status, including finished setup services
docker compose logs -f controller            # follow one service (api, controller, sandbox-launcher, egress-proxy, ...)
docker compose up -d                         # start, or apply .env changes
docker compose stop                          # stop; data is kept
docker compose up -d --build controller      # rebuild and restart one service after a code change
```

`docker compose down` removes the containers and keeps the data; `docker compose down --volumes` deletes **everything**, including Forgejo's
repositories, the databases and the OpenCode login.

## If something does not work

| Symptom | Likely cause and fix |
| --- | --- |
| `docker compose up` stops with "Set ... in .env" | a required password or secret is empty ([step 1](#1-get-the-code-and-configure-the-environment)) |
| the launcher restarts or logs a network error | the internal sandbox networks clash with one you use: change `SANDBOX_NET_BASE` and recreate |
| the ticket gets no comment at all | the webhook is missing or wrong ([project setup](project-setup.md#2-add-the-webhook)); in Forgejo use **Test Delivery**, then `docker compose logs api` |
| "FORGEJO_BOT_TOKEN is not configured" | step 4.3 was skipped or the controller was not recreated |
| "OpenCode is not signed in" on a ticket | step 5; no restart is needed afterwards |
| a run fails on the first agent step with an image error | the sandbox image for the project's `image` was not built (step 3) |

More in [Operations](operations.md#troubleshooting).
