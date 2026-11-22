# Working on dev-foundry

This file is for people and AI agents changing dev-foundry itself (not for projects it serves; see `templates/` for those). Start with `README.md` and `docs/architecture.md`.

## Layout
Code is in `src/` (one folder per service: the project, its `Dockerfile` and `README.md`) and its tests in `tests/` (same names). The solution file and the shared build settings (`Directory.Build.props`, `Directory.Packages.props`) are at the root, because both folders use them.
- `src/api`: receives and verifies git host webhooks (one route per host of `FACTORY_HOSTS`), starts the router workflow
- `src/controller`: the Temporal worker. `Agents/` (prompts and answer parsers per role; `Common/` has what every role uses), `Workflows/` (`Base/` is what every flow shares, `Tickets/` and `PullRequests/` are the flows, `Routing/` and `Requests/` hold the router and request types, `Comments/` the texts posted on tickets), `GitHost/` (the host port, router, kinds and activities; `Forgejo/` and `GitHub/` are its clients), `Workspace/` (`Git/` the checkout of a run with its usage and resume files, `Checks/` the change checks), `Project/` (`project.json` parsing, known keys, rules, limits), `Improvement/` (the weekly report), `Janitor/`
- `src/sandbox-launcher`, `src/egress-proxy`: run agents in disposable containers; limit what they can reach
- The **alert intake** (turns monitoring alerts into tickets) and the **demo** (a production with monitoring around it) are repositories of their own: `dev-foundry-alert-intake` and `dev-foundry-demo`. Nothing here builds or tests them. `deploy/forgejo/` is a Forgejo to try the factory on, its own Compose project
- `src/contracts`: types shared between services (`Hosts/` has the host list, the triggers and one webhook adapter per kind of host)
- `agents/`: the role files (`agents/agents/*.md`), the baseline `opencode.json`, and `standards/` (texts copied into roles)
- `sandboxes/`, `deploy/`: the agent images, the Compose stack (the egress allowlist, `profiles.json`, sits with its service in `src/egress-proxy/`)
- `templates/`: small working starter projects with every factory feature on (a test checks their settings with the real parser)
- `tools/`: `check-docs.py`, `check-clock.py`, `usage-report.py` (token totals from the usage log), `onboard-repo.py` (labels and webhook for a new repository, Forgejo or GitHub; its tests are in `tests/tools/`)
- `docs/`: documentation with diagrams (`docs/diagrams/*.dot` render to `docs/images/*.svg`)

## Commands (from the repository root)
- Unit tests of a service: `docker build --target test -f src/<service>/Dockerfile -t t . && docker run --rm t`
- Controller, fast tests only (no Temporal dev server, under a minute): `docker run --rm -e TEST_FILTER='Category!=Scenario' t`
- Controller, only the workflow scenarios: `docker run --rm -e TEST_FILTER='Category=Scenario' t` (about 7 minutes), or `python3 tools/run-scenarios.py` (the scenario classes split over 4 containers at once, about 2 minutes; `-j N` for another number, `--no-build` to reuse the image `t`)
- Launcher against the real Docker daemon (needs the base image built; builds and removes a throwaway image): `docker build --target test -f src/sandbox-launcher/Dockerfile -t t . && docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -e DEV_FOUNDRY_LIVE_DOCKER=1 -e TEST_FILTER='Category=Live' t`
- Is this machine's clock steady? (tests time out "after 5000ms" in a few seconds when it is not): `python3 tools/check-clock.py`
- Tests of the tools (a fake Forgejo/GitHub API, standard library only): `python3 -m unittest discover -s tests/tools`
- Documentation, links, settings and copies: `python3 tools/check-docs.py`
- Redraw diagrams after editing a `.dot` file: `sh docs/diagrams/render.sh`
- Rebuild and restart a service: `cd deploy && docker compose up -d --build <service>`

## Day to day
- **Loop:** change, run the fast tests (`TEST_FILTER='Category!=Scenario'`, under a minute), then the scenarios of the area you touched, then the full controller suite (about 5 to 12 minutes) before you call it done. `python3 tools/check-docs.py` must pass. There is no CI: nothing runs these for you, so run them yourself (the full list of checks is in [Operations](docs/operations.md#tests)). Rebuild the test image after every change: a stale image gives false failures.
- **Deploy locally:** `cd deploy && docker compose up -d --build <service>` (or without a service name for everything). The controller and the launcher are the ones that change most. After a rebuild, if the Temporal UI on port 8080 resets connections, `docker compose restart temporal-ui`.
- **Live check:** use a throwaway repository on the local Forgejo or on GitHub (a starter from `templates/` with its `.factory/project.json` copied in). A live run (a new ticket, `/retry`, `/qa`) spends the plan's tokens and writes to that repository, so ask the owner first. Read tickets and pull requests through the Forgejo API with the bot's token (the `token` of the `forgejo` host in `FACTORY_HOSTS`, in `deploy/.env`); never print or commit it.
- **This machine's clock:** under WSL2 the wall clock can step by several seconds, which makes timeouts fire early and tests fail with "after 5000ms". `python3 tools/check-clock.py` tells you whether it is steady.
- **Committing:** only when asked, one logical change per commit. Keep generated files (archives, build output, a record folder) out of git.
- **What is not built, or only partly,** is in `docs/operations.md` under "Known limitations".

## Rules that keep it correct
- **The controller decides, agents advise.** An agent returns one validated JSON verdict; code decides what happens. Do not move a decision into a prompt.
- **A new `project.json` key** needs parsing and tests in `Project/`, a place in the known-key lists of `Project/ProjectKeys.cs` (or it is warned about as unknown), an entry in `docs/project-setup.md`, and (if it has a factory default) a `FACTORY_DEFAULT_*` setting in `WorkerSettings`, `deploy/compose.yaml`, `deploy/.env.example` and `docs/configuration.md`. `check-docs.py` fails when one is missing.
- **A new environment variable** must be documented in `docs/configuration.md`.
- **A new kind of git host** (GitLab, ...) needs a webhook adapter in `src/contracts/Hosts/` (registered in `WebhookAdapters.All`, with tests of its payloads and signature), an `IGitHost` client in `src/controller/.../GitHost/<Kind>/` with tests, and one entry in `GitHostKinds` (its client, its API address and its git authorization header; a test checks that every adapter has one), a ticket client in the `dev-foundry-alert-intake` repository (on `RestTicketHost`, with an entry in `TicketHostKinds`) if alerts should open tickets there, and a column in `docs/git-hosts.md` (the bot account, token scopes, the webhook and its events, the permissions; its `GetPermissionAsync` decides what the `commands` rule can do there). Run it live on a real repository before you call it done: the first GitHub run found two bugs the fakes could not.
- **A new agent role** needs a file in `agents/agents/`, a row in `docs/configuration.md`, and a prompt builder and parser in `Agents/`. Put its limits in the role file next to its answer shape.
- **Shared role texts** (`agents/standards/engineering-floor.md`, `security-checklist.md`) are copied into role files between markers. Edit the source, paste it into the roles, and let `check-docs.py` confirm they match.
- **Tests:** workflow scenarios live in `Scenarios/` and use scripted agents with fake activities (`Harness.cs`). Tag any new test that needs the Temporal dev server with `[Trait("Category", "Scenario")]` and the `temporal` collection.
- **Repository images** (`.factory/Dockerfile`) are off by default and are accepted only through `RepoImagePolicy` in the launcher. Do not widen what it allows (instructions, flags, base images) without thinking about what a hostile repository could then run on the host's Docker.
- **Temporal determinism:** workflow code must not read the clock, environment or files directly. Read them in an activity, so a replayed run sees the same value.
- Never commit `deploy/.env` or anything with credentials.
