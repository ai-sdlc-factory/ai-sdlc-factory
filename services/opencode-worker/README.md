# OpenCode Demo Worker

[Project overview](../../README.md) | [Infrastructure](../../infra/README.md) | [Controller worker](../controller/README.md)

Runs a manual task through Temporal and OpenCode using ChatGPT Plus/Pro browser
authentication. No OpenAI API key is needed for this login route.

The demo generates `hello.py` with a greeting function and `test_hello.py` with
example tests. Their contents are returned as artifacts; the tests are not run automatically.

## Browser login

From `infra/`:

```bash
docker compose --profile opencode build opencode-worker
docker compose --profile opencode run --rm --service-ports opencode-login
```

Open the URL printed in the terminal in your Windows browser. Sign in and
authorize OpenCode, keeping the terminal open until login completes.
The callback listens on `http://localhost:1455`. Credentials stay in the
`opencode-data` Docker volume, outside `.env`, Git, and Temporal history.

If port `1455` is occupied, use device login instead (still completed in your browser):

```bash
docker compose --profile opencode run --rm --no-deps opencode-login \
  opencode auth login --provider openai --method "ChatGPT Pro/Plus (headless)"
```

Check authentication or list models:

```bash
docker compose --profile opencode run --rm --no-deps opencode-login opencode auth list
docker compose --profile opencode run --rm --no-deps opencode-login opencode models openai
```

## Run the demo

After signing in, run from `infra/`:

```bash
docker compose --profile opencode up -d --build controller opencode-worker
docker compose --profile opencode run --rm --no-deps opencode-demo
```

The command prints a workflow ID and JSON containing the generated files.
Inspect the workflow at http://localhost:8080.

Optional settings in `infra/.env` (defaults already apply to existing files):

```dotenv
OPENCODE_TASK_QUEUE=opencode-tasks
OPENCODE_MODEL=openai/gpt-6.1-sol
```

Choose a model available to your account. Access and usage depend on your ChatGPT plan.

## Execution

`OpenCodeDemoWorkflow` runs on the trusted controller worker and dispatches the
`run_opencode_task` activity to the separate OpenCode worker. It processes one
task at a time in a fresh temporary workspace, removed after completion.

The container has a read-only root filesystem, resource limits, and no Docker
socket or host repository mount. OpenCode receives no control-plane credentials.
File editing is allowed; shell commands, subagents, network tools, and access to
external directories are denied by the bundled configuration.

Tasks have a four-minute execution limit and no automatic activity retries.
Only the two expected UTF-8 artifacts are returned, limited to 16 KiB each.
The OpenCode login/session volume persists; deleting it removes the stored login.

Inspect logs with `docker compose --profile opencode logs -f opencode-worker`.

## Tests

From the repository root:

```bash
docker build --target test -f services/opencode-worker/Dockerfile -t dev-foundry/opencode-worker-test .
docker run --rm dev-foundry/opencode-worker-test
```

Include Temporal routing coverage after rebuilding the controller worker:

```bash
docker run --rm --network dev-foundry_agents \
  -e TEMPORAL_TEST_ADDRESS=temporal:7233 dev-foundry/opencode-worker-test
```

Automated xUnit tests use an explicit shell-script stub and consume no model usage.
The real authenticated model run is exercised by the demo command.

## References

- [OpenCode ChatGPT login](https://opencode.ai/docs/providers/#openai)
- [OpenCode CLI](https://opencode.ai/docs/cli/)
- [Sign in with ChatGPT](https://developers.openai.com/siwc/quickstart)
