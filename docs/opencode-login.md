# OpenCode login

[Documentation index](README.md) | [Getting started](getting-started.md#5-sign-the-agents-in)

The factory's agents run OpenCode with a ChatGPT Plus/Pro account (browser sign-in; no OpenAI API key).
You sign in once. The login is stored in the `opencode-data` Docker volume, outside `.env`, Git and
Temporal history, and the [sandbox launcher](../src/sandbox-launcher/README.md#model-access-and-the-opencode-login)
mounts it into the runs that call the model.

## Sign in

From `infra/`:

```bash
docker compose --profile opencode-tools run --rm --service-ports opencode-login
```

Open the URL printed in the terminal in your browser, sign in and authorize OpenCode, and keep the
terminal open until the login completes. The callback listens on `http://localhost:1455`.

If port `1455` is occupied, use device login instead (still completed in your browser):

```bash
docker compose --profile opencode-tools run --rm --no-deps opencode-login \
  opencode auth login --provider openai --method "ChatGPT Pro/Plus (headless)"
```

Check the login or list the models your account can use:

```bash
docker compose --profile opencode-tools run --rm --no-deps opencode-login opencode auth list
docker compose --profile opencode-tools run --rm --no-deps opencode-login opencode models openai
```

The model each agent uses is set in its role file under [`agents/agents/`](../agents/README.md#choosing-the-model-per-role) (with a default in
[`agents/opencode.json`](../agents/opencode.json)); choose one your plan includes. Usage depends on your ChatGPT plan.

## How it is shared

The login helper and the sandboxes all run as the factory user (`FACTORY_UID`). Before the login starts,
the one-shot `opencode-data-init` service changes the volume's owner to that user, so a volume created by
an older version is fixed the first time you sign in. Only runs that ask for model access get the volume,
and only `auth.json` is linked into OpenCode's data directory; the project's own checks never see it.

## Troubleshooting

**"OpenCode is not signed in"** appears on a ticket (or as exit code 78 in a sandbox) when the volume
holds no login. Sign in as above; the next ticket picks it up. You do not need to restart anything.

The login is lost whenever the volume is deleted, for example by `docker compose down -v`, a prune that
removes unused volumes, or a Docker Desktop reset.

## References

- [OpenCode ChatGPT login](https://opencode.ai/docs/providers/#openai)
- [OpenCode CLI](https://opencode.ai/docs/cli/)
- [Sign in with ChatGPT](https://developers.openai.com/siwc/quickstart)
