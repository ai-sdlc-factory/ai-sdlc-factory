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
- **environment variables** starting with `OPENCODE_` or `FACTORY_`.

Everything else is fixed in `ContainerSpec` and tested in `ContainerSpecTests`: non-root user (the
workspace owner), read-only root filesystem with a 1 GB `/tmp`, all capabilities dropped,
`no-new-privileges`, no privileged mode, no devices, a PID limit, memory and CPU limits, and no
mounts except the workspace and, if `FACTORY_GLOBAL_CONFIG` is set, that directory read-only at
`/etc/factory`. Sandboxes join the `dev-foundry_sandbox` network, which the launcher creates. It
has outbound internet (agents need their model provider) and contains no factory service.

Docker Desktop creates a missing bind-mount source as root, so the launcher checks the workspace
exists, and is not a symlink, before it asks Docker to mount it.

## Images

Images are built outside the launcher:

```bash
cd infra
docker compose --profile images build sandbox-image-dotnet
```

[`sandboxes/dotnet/`](../../sandboxes/dotnet/Dockerfile) holds the .NET image: SDK, git and the
pinned OpenCode binary. Add another language by adding `sandboxes/<name>/Dockerfile` and one entry
to `SANDBOX_IMAGES`.

## Behavior

- A command that exits non-zero is a normal result (`exit_code`), not an activity failure.
- A command that runs past its timeout is killed; the result has `timed_out: true`.
- Output is limited to 64 KB per stream; `output_truncated` says when it was cut.
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
