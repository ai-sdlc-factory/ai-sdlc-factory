# OpenCode login

[Documentation index](README.md) | [Getting started](getting-started.md#5-sign-the-agents-in)

The factory's agents run OpenCode with a ChatGPT Plus/Pro account (browser sign-in; no OpenAI API key; [other ways](#other-ways-to-sign-in) are untested).
You sign in once. The login is stored in the `opencode-data` Docker volume, outside `.env`, Git and
Temporal history, and the [sandbox launcher](../src/sandbox-launcher/README.md#model-access-and-the-opencode-login)
mounts it into the runs that call the model.

## Sign in

From `deploy/`:

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

## Other ways to sign in

**The factory was built and tested with the ChatGPT Plus/Pro sign-in described above, and only that.** Everything below is a starting point that has **not been tried**: the code does not forbid it, but nothing here was run with it, so expect to fix things.

**An OpenAI API key.** The login volume does not care how `auth.json` was made. OpenCode's `opencode auth login --provider openai` also offers a method to enter an API key by hand; run it in the same helper as above and it writes to the same volume. Then the sandboxes must be allowed to reach the API: the egress allowlist ([`profiles.json`](../src/egress-proxy/ALLOWLIST.md)) lets the `model` profile reach only `chatgpt.com` and `auth.openai.com`, and an API key calls `api.openai.com`. Add that host to the profile (in your own file, set with `EGRESS_PROFILES`, as the allowlist guide describes) and restart the proxy. With a key, OpenCode reports real cost figures and the provider bills per token, so the [token budget and the daily cap](operations.md#token-limits) matter more than with a plan; they have never been run with a key.

**Another provider** (Anthropic, a local or hosted model). Three places say "OpenAI": `enabled_providers` in [`agents/opencode.json`](../agents/opencode.json), the `model:` line of every role file in [`agents/agents/`](../agents/README.md#choosing-the-model-per-role), and the egress hosts of the `model` profile. Change all three to the provider's, sign in with `opencode auth login --provider <name>`, and check each role with `opencode debug agent <role>`. Role files and prompts were written and checked against the OpenAI models named in them; another model may follow the JSON answer shapes less reliably, and the controller rejects an answer that does not parse.

If you get one of these working, a pull request with what you changed and what you hit is welcome here.

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
