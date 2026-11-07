# Sandbox Launcher

[Documentation index](../../docs/README.md) | [Architecture](../../docs/architecture.md) | [Infrastructure](../../infra/README.md) | [Egress proxy](../egress-proxy/README.md)

Starts disposable containers for the factory's agents. A workflow asks for "run this command in
the `dotnet` image against workspace X"; the launcher starts one container, waits for it, returns
the exit code and output, and always removes the container. It is a Temporal activity worker on
the `sandbox` task queue (activity `sandbox_run`); it has no HTTP port.

## Why it is a separate service

Starting containers needs the Docker socket, which is effectively root on the host. Only this
service has it. It has no Forgejo token and no database access, runs as an unprivileged user (the
socket is reached through the `DOCKER_GID` group), and runs with a read-only filesystem and no
capabilities itself. Its code can only issue a handful of fixed Docker calls.

## What a request can and cannot choose

A request (`SandboxRunRequest` in `libs/DevFoundry.Contracts`) can choose:

- an **image alias** from the allowlist in `SANDBOX_IMAGES` (`alias=image;alias=image`),
- a **workspace** directory name under `FACTORY_WORKSPACE_ROOT` that already exists,
- whether the workspace is **writable**,
- the **command** (an argument list, no shell), a **timeout** (capped by `SANDBOX_MAX_RUN_SECONDS`),
- **environment variables** starting with `OPENCODE_` or `FACTORY_`,
- whether the run needs **model access** (the OpenCode login, below),
- a **role** (`developer`, `gate`, ...), only used to label the container.

Every container is labelled `dev-foundry.sandbox=true`, `dev-foundry.workspace=<workspace>` (the workflow id, so it
names the repository and ticket) and `dev-foundry.role=<role>`, so with several runs in parallel
`docker ps --filter label=dev-foundry.workspace=<id>` shows what belongs to which ticket.

Everything else is fixed in `ContainerSpec` and tested in `ContainerSpecTests`: non-root user (the
workspace owner), read-only root filesystem with a 1 GB `/tmp`, all capabilities dropped,
`no-new-privileges`, no privileged mode, no devices, a PID limit, memory and CPU limits, and no
mounts except the workspace and, if `FACTORY_GLOBAL_CONFIG` is set, that directory read-only at
`/etc/factory`. The network is chosen per run (see Network below); a run that asks for none has no
network interface at all.

Docker Desktop creates a missing bind-mount source as root, so the launcher checks the workspace
exists, and is not a symlink, before it asks Docker to mount it.

## Network

A request names an **egress profile** in `network`: `none` (the default: no network interface),
`model`, `packages` or `developer` (`SandboxNetworks` in the contracts). Anything else is rejected.
A profile puts the container on that profile's **internal** Docker network (no route out, no external
DNS) and sets `HTTPS_PROXY` and friends to that profile's address on the [egress
proxy](../egress-proxy/README.md), which tunnels HTTPS only to the hosts the profile lists
([`egress/`](../../egress/README.md)). Tools that ignore the proxy settings cannot connect at all. The
proxy variables are fixed by the launcher; a request cannot set them (only `OPENCODE_` and `FACTORY_`
variables are accepted), and the launcher's `OPENCODE_DISABLE_*` settings (no model-list fetch, no
update check, no language-server downloads) are always last, so a request cannot undo them.

`SANDBOX_EGRESS_MODE` (`enforce`, `audit` or `open`, from `SANDBOX_EGRESS` in `.env`) switches the
restrictions: in `open` mode every profile maps to one ordinary internet-connected network
(`SANDBOX_OPEN_NETWORK`, created by the launcher) with no proxy, and the launcher logs a warning at start;
`none` stays none. `audit` is the proxy's own switch, so the launcher treats it like `enforce`. See the
[egress guide](../../egress/README.md#modes-and-your-own-rules).

`SANDBOX_NETWORKS` and `SANDBOX_PROXIES` map each profile to its network and proxy address; both are set
in `infra/compose.yaml`. The factory picks the profile by role: read-only agents get `model`, the
developer `developer`, the project's checks `packages`.

## Model access and the OpenCode login

Only a request with `needs_model_auth: true` gets the login. The launcher then mounts the Docker
volume named by `SANDBOX_AUTH_VOLUME` (`dev-foundry_opencode-data`, the same volume the
`opencode-login` service fills; see the [login guide](../../docs/opencode-login.md)) at `/auth` and sets `FACTORY_REQUIRE_AUTH=1`. Lint, build and test
runs never see the credentials. If the volume does not exist the activity fails at once with
"OpenCode is not signed in"; if it exists but holds no `auth.json`, the sandbox exits with code 78.

Inside the sandbox, [`factory-entry.sh`](../../sandboxes/common/factory-entry.sh) links only
`auth.json` into OpenCode's per-run data directory. OpenCode keeps its sessions and logs next to the
login, so mounting the whole volume as its data directory would mix the history of every ticket.
The volume is mounted read-write by default so OpenCode can store a refreshed token; set
`SANDBOX_AUTH_READONLY=true` to forbid that, at the cost of a login that stops working once the token
expires. An agent can always read the token it runs with, which is why its network is limited to an
allowlist (see Network).

The login volume is owned by the factory user (`FACTORY_UID`). The one-shot `opencode-data-init`
service sets that up and runs automatically before the login helper.

## Agent definitions

Set `FACTORY_GLOBAL_CONFIG` to the absolute path of the repository's [`agents/`](../../agents/README.md)
folder. It is mounted read-only at `/etc/factory`; the entrypoint copies it to a writable place and
points OpenCode at it, because OpenCode writes into its config directory.

## Images

Images are built outside the launcher:

```bash
cd infra
docker compose --profile images build sandbox-image-dotnet
# combined images for projects that need more than one toolchain
docker compose --profile images build sandbox-image-dotnet-node sandbox-image-python-node sandbox-image-full
```

[`sandboxes/dotnet/`](../../sandboxes/dotnet/Dockerfile) holds the .NET image (SDK, git, ripgrep and the
pinned OpenCode binary) [`sandboxes/python/`](../../sandboxes/python/Dockerfile) the Python 3.12 one
(plus pytest and ruff), and [`sandboxes/node/`](../../sandboxes/node/Dockerfile) the Node.js 22 LTS one (npm,
and corepack for pnpm/yarn; projects install their own dependencies through the egress proxy, and
`node --test` needs nothing extra). The combined images [`dotnet-node/`](../../sandboxes/dotnet-node/Dockerfile) (.NET SDK + Node.js), [`python-node/`](../../sandboxes/python-node/Dockerfile) (Python + Node.js) and [`full/`](../../sandboxes/full/Dockerfile) (.NET + Python + Node.js) add Node.js by copying it from the official `node:22-bookworm-slim` image. All of them use the shared entrypoint in `sandboxes/common/`, which creates `HOME` and the XDG directories in the container's tmpfs (the .NET CLI fails if they are missing), prepares OpenCode's settings and login, and then runs the command.
Add another language by adding `sandboxes/<name>/Dockerfile`, a build entry for it (profile `images`) in `infra/compose.yaml`, and one `alias=image` pair to `SANDBOX_IMAGES`; projects then pick it with `"image": "<alias>"`.
The image must contain git, the OpenCode binary, the language's tooling and `/usr/local/bin/factory-entry`; look at the existing Dockerfiles.

## Behavior

- A command that exits non-zero is a normal result (`exit_code`), not an activity failure.
- A command that runs past its timeout is killed; the result has `timed_out: true`.
- Output is limited to the last 512 KB per stream (the end is what matters); `output_truncated` says when the start was cut.
- Policy violations fail the activity without retries. Failures before the command starts
  (Docker unreachable, create or start errors) are retried only when Docker itself failed.
  Once the command may have run, nothing is retried, because a run can consume model usage.
- On start the launcher checks Docker and that every egress network exists (it refuses to run
  otherwise), and removes sandbox containers left by a previous launcher process.

## Tests

From the repository root:

```bash
docker build --target test -f services/sandbox-launcher/Dockerfile -t dev-foundry/sandbox-launcher-test .
docker run --rm dev-foundry/sandbox-launcher-test
```

The tests cover the request policy, the fixed container settings, timeout, cancellation and
cleanup against a fake Docker, and the log parser. Real containers are exercised by creating a
`feature`, `bug` or `incident` ticket (see [Getting started](../../docs/getting-started.md#7-connect-your-first-repository)).
