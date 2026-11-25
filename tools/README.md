# Tools

[Documentation index](../docs/README.md) | [Operations](../docs/operations.md) | [Contributing rules](../AGENTS.md)

Small standalone scripts. They use only the Python 3 standard library (and Docker where noted), and run from the repository root.

| Script | For | What it does |
| --- | --- | --- |
| [`check-docs.py`](check-docs.py) | contributors | Checks that the documentation still matches the repository: every relative link and anchor, every diagram has its image and the other way round, every setting and environment variable the code reads is documented, every `project.json` key, agent role, result status and label. Exits 1 on any problem. Run it before every commit that touches code or docs. |
| [`run-scenarios.py`](run-scenarios.py) | contributors | Runs the controller's workflow scenarios in several containers at once, so they take about three minutes instead of seven. `-j N` sets the number of containers, `--no-build` reuses the test image `t` (rebuild it yourself after every change). Needs Docker. |
| [`check-clock.py`](check-clock.py) | contributors on WSL 2 | Samples the wall clock against the monotonic clock and reports when the wall clock steps. A clock that jumps by seconds makes tests fail with "timed out after 5000ms" although they take milliseconds. Exit code 1 if it stepped. |
| [`usage-report.py`](usage-report.py) | operators | Totals of the usage log: tokens used by finished runs, grouped by repository, day, month, kind of run, status or ticket (`--by`), filtered with `--since`, `--until`, `--repo` and `--kind`. It reads the log the controller keeps under `FACTORY_WORKSPACE_ROOT` (the root named in `deploy/.env`), or a file given with `--file`. Repositories on a host other than `forgejo` are written `host:owner/repo`; `--repo owner/repo` matches the repository on any host, `--repo host:owner/repo` only that host's. `--by repository` is the same as `--by repo`. |
| [`onboard-repo.py`](onboard-repo.py) | operators | Sets a **Forgejo or GitHub** repository up for the factory: creates the labels and the webhook and checks `.factory/project.json` (that it is JSON and, when Docker and the controller image are there, that the factory can use it, with the same reader the runs use). On a GitHub host whose `commands` rule still lets every account in (none, or only `anyone`, `allow` or `refusal`) it also prints a note that anyone may use commands, for a **public** repository or one whose visibility GitHub did not say; a private repository gets no note. It reads the host's kind and webhook secret from `FACTORY_HOSTS` in `deploy/.env` and never prints a secret or a token. `--host <name>` picks a host (default: the first), `--dry-run` shows what it would do; it is safe to run again. **Forgejo** uses the bot's own token, which is enough for the labels; managing the webhook needs a repository administrator, so give an administrator's token with `--token-env` or make the bot an administrator of the repository. **GitHub** needs *your* token (`GITHUB_TOKEN`, `--token-env`, or `gh auth login`; the bot's has no right to webhooks and should not get one) and `--public-url`, the factory's address as GitHub sees it. Details: `--help` and [Connecting a git host](../docs/git-hosts.md#5-the-webhook). Tests: `python3 -m unittest discover -s tests/tools`. |
| [`github-hook-ips.py`](github-hook-ips.py) | operators | Prints the addresses GitHub sends webhooks from (the `hooks` list of GitHub's meta endpoint) as a Cloudflare rule expression, a Caddy matcher or nginx `allow` lines, so only GitHub can reach the webhook route. See [Letting GitHub reach the factory](../docs/exposing-webhooks.md). Sends no credentials. |

## Examples

```bash
python3 tools/check-docs.py                                   # before committing
python3 tools/run-scenarios.py --no-build                     # all workflow scenarios, in parallel
python3 tools/usage-report.py --by day --since 2026-11-01     # what the factory spent
python3 tools/onboard-repo.py acme/shop --dry-run             # what it would change on the first host (Forgejo)
GITHUB_TOKEN=... python3 tools/onboard-repo.py acme/shop --host github --public-url https://factory.example.org --dry-run
```

The commands for running the unit tests of each service are in [AGENTS.md](../AGENTS.md#commands-from-the-repository-root).
