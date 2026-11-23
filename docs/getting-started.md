# Getting started: setting up the factory

[Documentation index](README.md) | next: [Setting up a project](project-setup.md)

This guide takes you from a clean machine to a running factory. It is done **once**. Afterwards, each repository the factory should
serve needs a [short setup](project-setup.md).

## What you need

| Requirement | Notes |
| --- | --- |
| Windows with **WSL 2** (Ubuntu) and **Docker Desktop** with WSL integration enabled, or any Linux with Docker Engine and Compose v2 | The stack is built and tested on WSL. Docker Compose runs everything. If `docker` is "not found" inside WSL, turn the WSL integration for your distribution back on in Docker Desktop (Settings, Resources, WSL integration). |
| About 10 GB of disk and 8 GB of RAM for Docker | The sandboxes are limited to 2 GB and 2 CPUs each, and 2 run at once. |
| A **ChatGPT Plus/Pro account** | The agents use OpenCode with a ChatGPT sign-in; there is no OpenAI API key (an API key or another provider is [possible but untested](opencode-login.md#other-ways-to-sign-in)). Usage counts against your plan. |
| `git`, `openssl` and `python3` on the host | `git` to clone, `openssl` for the passwords below (`openssl rand -hex 24`), `python3` (standard library only) for the scripts in [`tools/`](../tools/README.md). You do **not** need a .NET SDK, Node or Python packages: everything is built and tested in Docker. |
| A **git host** with an account for the bot | Your own Forgejo (the bundled one, in [`deploy/forgejo/`](../deploy/forgejo/README.md), is enough to try it) or GitHub. Creating the bot on Forgejo needs administrator rights; every repository needs a webhook and the bot as a collaborator ([Connecting a git host](git-hosts.md)). |
| A way for the host to reach the factory's `api` | The bundled Forgejo reaches it over Docker's network. **GitHub (or any host outside your machine) needs a public HTTPS address for the webhook**: a server with a domain, or on a laptop a tunnel to port 8000. [Letting GitHub reach the factory](exposing-webhooks.md) compares the ways (a named Cloudflare Tunnel is permanent and needs no open port); the details of the webhook are in [Connecting a git host](git-hosts.md#5-the-webhook). |
| Free local ports | `8000` (api), `8080` (Temporal UI), `7233`, `3000` for the bundled Forgejo, and `5432` and `5050` only with the optional PostgreSQL (and pgAdmin); `1455` for the ChatGPT sign-in. Each can be changed in `deploy/.env` (see the [services table](../deploy/README.md#services)). |
| Internet access for Docker image builds | The agents' own network access is restricted separately ([egress](../src/egress-proxy/ALLOWLIST.md)). |

## 1. Get the code and configure the environment

```bash
git clone https://github.com/ai-sdlc-factory/ai-sdlc-factory.git
cd ai-sdlc-factory/deploy
cp -n .env.example .env      # keeps an existing .env
chmod 600 .env
```

Open `deploy/.env` and set the values below. Everything else has a working default; the full list is in [Configuration](configuration.md).

| Setting | Required | What to put |
| --- | --- | --- |
| `POSTGRES_PASSWORD`, `TEMPORAL_DB_PASSWORD` | no | only for the optional PostgreSQL-backed Temporal (`COMPOSE_FILE=compose.yaml:compose.postgres.yaml`, see [Deployment](../deploy/README.md#temporal-database)); the default is a SQLite file and needs neither |
| `FACTORY_HOSTS` | **yes** | the git hosts the factory serves, as one line of JSON. For the bundled Forgejo, with the token still empty: `[{"name":"forgejo","url":"http://forgejo:3000","token":"","webhook_secret":"<openssl rand -hex 32>"}]`. The secret is what you paste into each repository's webhook later. For GitHub, or several hosts, see [Configuration](configuration.md#factory_hosts) |
| `FACTORY_WORKSPACE_ROOT` | **yes** | an absolute host path for the checkouts. **Create it first**: `mkdir -p /var/tmp/ai-sdlc-factory-workspaces` (Docker would create it as root otherwise) |
| `FACTORY_UID`, `FACTORY_GID` | **yes** | your user: `id -u`, `id -g` |
| `DOCKER_GID` | **yes** | the group of the Docker socket: `stat -c %g /var/run/docker.sock` |
| `FACTORY_GLOBAL_CONFIG` | **yes, in practice** | the absolute path of this repository's `agents/` folder, for example `/home/you/ai-sdlc-factory/agents`. Without it the sandboxes run without the role files and the agents cannot work |
| `SANDBOX_EGRESS` | no | `enforce` (default), `audit` or `open`: how strictly the sandboxes' internet access is limited |

The `.env` file is ignored by Git. Never commit it.

## 2. Start the stack

```bash
docker compose up -d --build
docker compose ps -a
```

(The factory starts no git host. Step 4 sets one up.)

The first start creates the Temporal namespace. `temporal-namespace` should exit with code `0` (with the PostgreSQL option, `temporal-schema` too); everything else keeps running.
Check the stack:

| Check | Expected |
| --- | --- |
| `docker compose ps -a` | all services `running` or `healthy`; the Temporal setup service `temporal-namespace` `exited (0)` (and `temporal-schema` with the PostgreSQL option) |
| http://localhost:8000/health/ready | `200`: the api can reach Temporal |
| http://localhost:8080 | the Temporal UI |

## 3. Build the sandbox images

The agents run in images for each language your projects use. Build them once (they are not part of `up`):

```bash
docker compose --profile images build sandbox-image-dotnet sandbox-image-python sandbox-image-node
# only if a project needs two toolchains (for example a .NET API with a React front end)
docker compose --profile images build sandbox-image-dotnet-node sandbox-image-python-node sandbox-image-full
# only if a project uses /qa with a UI suite (`verify.image` is `browser`): the browser image is built ON the full image,
# so build full first (above) and this one in a command of its own; in the same command it fails with "pull access denied"
docker compose --profile images build sandbox-image-browser
```

Build only the ones you need; a project that asks for an image that does not exist fails its first agent run with a message that names the image and the command that builds it (the error from Docker alone says only "No such image").
Adding another language is described in the [launcher guide](../src/sandbox-launcher/README.md#images).

## 4. Set up a git host and the factory's bot account

The factory works with the hosts listed in `FACTORY_HOSTS`. Everything a host needs (the bot account, the token and its permissions, webhooks and their events, labels, who may steer) is in [Connecting a git host](git-hosts.md); this step is the short path for the bundled Forgejo. The quickest host to try it on is a Forgejo of your own, which runs as a project of its own in [`deploy/forgejo/`](../deploy/forgejo/README.md); for GitHub, create the bot account and token as described under [Several git hosts](operations.md#several-git-hosts), put them into `FACTORY_HOSTS` and skip to step 5.

1. Start Forgejo (the factory's stack must be up, because Forgejo joins two of its networks), open http://localhost:3000, finish the installation and create an **administrator** account:

   ```bash
   cd forgejo && cp -n .env.example .env && docker compose up -d && cd ..
   ```

2. Create the **bot account** the factory uses to comment, push branches and open pull requests, so it never uses a personal account: [the commands](../deploy/forgejo/README.md#the-factory-bot-account) print its token.
3. Put the printed token into the `"token"` of the Forgejo entry of `FACTORY_HOSTS` in `deploy/.env` and recreate the controller:

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
docker build --target test -f src/controller/Dockerfile -t ai-sdlc-factory/controller-test . && docker run --rm ai-sdlc-factory/controller-test
# full pipeline test against the running stack (from deploy/)
docker compose --profile test run --build --rm --no-deps integration-tests
```

## 7. Connect your first repository

Follow [Setting up a project](project-setup.md). It takes five minutes: bot access, a webhook, labels and a small `.factory/project.json`. Then create a
ticket, add the `feature` label, and watch the comments appear.

## Everyday commands

Run from `deploy/`:

```bash
docker compose ps -a                         # status, including finished setup services
docker compose logs -f controller            # follow one service (api, controller, sandbox-launcher, egress-proxy, ...)
docker compose up -d                         # start, or apply .env changes
docker compose stop                          # stop; data is kept
docker compose up -d --build controller      # rebuild and restart one service after a code change
```

`docker compose down` removes the containers and keeps the data; `docker compose down --volumes` deletes **everything** in this stack, including the databases and the OpenCode login.

## If something does not work

| Symptom | Likely cause and fix |
| --- | --- |
| `docker compose up` stops with "Set ... in .env" | a required password or secret is empty ([step 1](#1-get-the-code-and-configure-the-environment)) |
| the launcher restarts or logs a network error | the internal sandbox networks clash with one you use: change `SANDBOX_NET_BASE` and recreate |
| the ticket gets no comment at all | the webhook is missing or wrong ([project setup](project-setup.md#2-add-the-webhook)); in Forgejo use **Test Delivery**, then `docker compose logs api` |
| "The Forgejo host has no token in FACTORY_HOSTS" | step 4.3 was skipped or the controller was not recreated |
| "OpenCode is not signed in" on a ticket | step 5; no restart is needed afterwards |
| a run fails on the first agent step with an image error | the sandbox image for the project's `image` was not built (step 3) |

More in [Operations](operations.md#troubleshooting).
