# Working on dev-foundry

This file is for people and AI agents changing dev-foundry itself (not for projects it serves; see `docs/examples/` for those). Start with `README.md` and `docs/architecture.md`.

## Layout
- `services/api`: receives and verifies Forgejo webhooks, starts the router workflow
- `services/controller`: the Temporal worker. `Agents/` (prompts and answer parsers per role), `Workflows/` (the flows; `Routing/` and `Requests/` hold the router and request types), `Forgejo/`, `Workspace/` (git, change checks, the usage and resume files), `Project/` (`project.json` parsing, known keys, rules, limits), `Improvement/` (the weekly report), `Janitor/`, `Common/`
- `services/sandbox-launcher`, `services/egress-proxy`: run agents in disposable containers; limit what they can reach
- `services/alert-intake`: turns monitoring alerts into Forgejo tickets (fixed rules, no AI; a dry run is the default)
- `demo/`: a local "production" for the weather app with Prometheus, Alertmanager, Grafana and Loki, and the scripts to set it up, deliver code to it and break it on purpose
- `libs/DevFoundry.Contracts`: types shared between services
- `agents/`: the role files (`agents/agents/*.md`), the baseline `opencode.json`, and `standards/` (texts copied into roles)
- `sandboxes/`, `egress/`, `infra/`: images, allowlist profiles, Compose stack
- `templates/`: small working starter projects with every factory feature on (a test checks their settings with the real parser)
- `tools/`: `check-docs.py`, `check-clock.py`, `usage-report.py` (token totals from the usage log), `onboard-repo.py` (labels and webhook for a new repository)
- `docs/`: documentation with diagrams (`docs/diagrams/*.dot` render to `docs/images/*.svg`)

## Commands (from the repository root)
- Unit tests of a service: `docker build --target test -f services/<service>/Dockerfile -t t . && docker run --rm t`
- Controller, fast tests only (no Temporal dev server, under a minute): `docker run --rm -e TEST_FILTER='Category!=Scenario' t`
- Controller, only the workflow scenarios: `docker run --rm -e TEST_FILTER='Category=Scenario' t`
- Launcher against the real Docker daemon (needs the base image built; builds and removes a throwaway image): `docker build --target test -f services/sandbox-launcher/Dockerfile -t t . && docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -e DEV_FOUNDRY_LIVE_DOCKER=1 -e TEST_FILTER='Category=Live' t`
- Is this machine's clock steady? (tests time out "after 5000ms" in a few seconds when it is not): `python3 tools/check-clock.py`
- Documentation, links, settings and copies: `python3 tools/check-docs.py`
- Redraw diagrams after editing a `.dot` file: `sh docs/diagrams/render.sh`
- Rebuild and restart a service: `cd infra && docker compose up -d --build <service>`

## Day to day
- **Loop:** change, run the fast tests (`TEST_FILTER='Category!=Scenario'`, under a minute), then the scenarios of the area you touched, then the full controller suite (about 5 to 12 minutes) before you call it done. `python3 tools/check-docs.py` must pass. Rebuild the test image after every change: a stale image gives false failures.
- **Deploy locally:** `cd infra && docker compose up -d --build <service>` (or without a service name for everything). The controller and the launcher are the ones that change most. After a rebuild, if the Temporal UI on port 8080 resets connections, `docker compose restart temporal-ui`.
- **Live check:** the `valters/weather` repository on the local Forgejo is the fixture (a .NET API with SQLite and a Vite/React front end). Its `.factory/project.json` has every feature on, including `verify` with an `api` and a `ui` suite. A live run (a new ticket, `/retry`, `/qa`) spends the plan's tokens and writes to that repository, so ask the owner first. Read tickets and pull requests through the Forgejo API with the bot's token (`FORGEJO_BOT_TOKEN` in `infra/.env`); never print or commit it.
- **This machine's clock:** under WSL2 the wall clock can step by several seconds, which makes timeouts fire early and tests fail with "after 5000ms". `python3 tools/check-clock.py` tells you whether it is steady.
- **Committing:** only when asked, one logical change per commit. Keep generated files (archives, build output, a record folder) out of git.
- **What is not built, or only partly,** is in `docs/operations.md` under "Known limitations".

## Rules that keep it correct
- **The controller decides, agents advise.** An agent returns one validated JSON verdict; code decides what happens. Do not move a decision into a prompt.
- **A new `project.json` key** needs parsing and tests in `Project/`, a place in the known-key lists of `Project/ProjectKeys.cs` (or it is warned about as unknown), an entry in `docs/project-setup.md`, and (if it has a factory default) a `FACTORY_DEFAULT_*` setting in `WorkerSettings`, `infra/compose.yaml`, `infra/.env.example` and `docs/configuration.md`. `check-docs.py` fails when one is missing.
- **A new environment variable** must be documented in `docs/configuration.md`.
- **A new agent role** needs a file in `agents/agents/`, a row in `docs/configuration.md`, and a prompt builder and parser in `Agents/`. Put its limits in the role file next to its answer shape.
- **Shared role texts** (`agents/standards/engineering-floor.md`, `security-checklist.md`) are copied into role files between markers. Edit the source, paste it into the roles, and let `check-docs.py` confirm they match.
- **Tests:** workflow scenarios live in `Scenarios/` and use scripted agents with fake activities (`Harness.cs`). Tag any new test that needs the Temporal dev server with `[Trait("Category", "Scenario")]` and the `temporal` collection.
- **Repository images** (`.factory/Dockerfile`) are off by default and are accepted only through `RepoImagePolicy` in the launcher. Do not widen what it allows (instructions, flags, base images) without thinking about what a hostile repository could then run on the host's Docker.
- **Temporal determinism:** workflow code must not read the clock, environment or files directly. Read them in an activity, so a replayed run sees the same value.
- Never commit `infra/.env` or anything with credentials.
