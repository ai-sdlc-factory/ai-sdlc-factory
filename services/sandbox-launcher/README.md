# Sandbox Launcher

[Project overview](../../README.md) | [Infrastructure](../../infra/README.md) | [Feature factory design](../../docs/feature-factory.md)

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
- whether the run needs **model access** (the OpenCode login, below).

Everything else is fixed in `ContainerSpec` and tested in `ContainerSpecTests`: non-root user (the
workspace owner), read-only root filesystem with a 1 GB `/tmp`, all capabilities dropped,
`no-new-privileges`, no privileged mode, no devices, a PID limit, memory and CPU limits, and no
mounts except the workspace and, if `FACTORY_GLOBAL_CONFIG` is set, that directory read-only at
`/etc/factory`. Sandboxes join the `dev-foundry_sandbox` network, which the launcher creates. It
has outbound internet (agents need their model provider) and contains no factory service.

Docker Desktop creates a missing bind-mount source as root, so the launcher checks the workspace
exists, and is not a symlink, before it asks Docker to mount it.

## Model access and the OpenCode login

Only a request with `needs_model_auth: true` gets the login. The launcher then mounts the Docker
volume named by `SANDBOX_AUTH_VOLUME` (`dev-foundry_opencode-data`, the same volume the
`opencode-login` service fills) at `/auth` and sets `FACTORY_REQUIRE_AUTH=1`. Lint, build and test
runs never see the credentials. If the volume does not exist the activity fails at once with
"OpenCode is not signed in"; if it exists but holds no `auth.json`, the sandbox exits with code 78.

Inside the sandbox, [`factory-entry.sh`](../../sandboxes/dotnet/factory-entry.sh) links only
`auth.json` into OpenCode's per-run data directory. OpenCode keeps its sessions and logs next to the
login, so mounting the whole volume as its data directory would mix the history of every ticket.
The volume is mounted read-write by default so OpenCode can store a refreshed token; set
`SANDBOX_AUTH_READONLY=true` to forbid that, at the cost of a login that stops working once the token
expires. An agent can always read the token it runs with, so keep the sandbox network and the
allowed images tight.

The login volume is owned by the factory user (`FACTORY_UID`). The one-shot `opencode-data-init`
service sets that up and runs automatically before the login, demo worker and launcher flows.

## Agent definitions

Set `FACTORY_GLOBAL_CONFIG` to the absolute path of the repository's [`agents/`](../../agents/README.md)
folder. It is mounted read-only at `/etc/factory`; the entrypoint copies it to a writable place and
points OpenCode at it, because OpenCode writes into its config directory.

## Images

Images are built outside the launcher:

```bash
cd infra
docker compose --profile images build sandbox-image-dotnet
```

[`sandboxes/dotnet/`](../../sandboxes/dotnet/Dockerfile) holds the .NET image (SDK, git, ripgrep and the
pinned OpenCode binary) and [`sandboxes/python/`](../../sandboxes/python/Dockerfile) the Python 3.12 one
(plus pytest and ruff). Both use the shared entrypoint in `sandboxes/common/`. Add another language by
adding `sandboxes/<name>/Dockerfile` and one entry to `SANDBOX_IMAGES`.

## Behavior

- A command that exits non-zero is a normal result (`exit_code`), not an activity failure.
- A command that runs past its timeout is killed; the result has `timed_out: true`.
- Output is limited to the last 512 KB per stream (the end is what matters); `output_truncated` says when the start was cut.
- Policy violations fail the activity without retries. Failures before the command starts
  (Docker unreachable, create or start errors) are retried only when Docker itself failed.
  Once the command may have run, nothing is retried, because a run can consume model usage.
- On start the launcher checks Docker, creates the sandbox network, and removes sandbox
  containers left by a previous launcher process.

## Tests

From the repository root:

```bash
docker build --target test -f services/sandbox-launcher/Dockerfile -t dev-foundry/sandbox-launcher-test .
docker run --rm dev-foundry/sandbox-launcher-test
```

The tests cover the request policy, the fixed container settings, timeout, cancellation and
cleanup against a fake Docker, and the log parser. Real containers are exercised by creating a
`feature` ticket (see the [controller guide](../controller/README.md#feature-tickets)).
