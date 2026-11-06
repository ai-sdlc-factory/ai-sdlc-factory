# Working on dev-foundry

This file is for people and AI agents changing dev-foundry itself (not for projects it serves; see `docs/examples/` for those). Start with `README.md` and `docs/architecture.md`.

## Layout
- `services/api`: receives and verifies Forgejo webhooks, starts the router workflow
- `services/controller`: the Temporal worker. `Agents/` (prompts and answer parsers per role), `Workflows/` (the flows; `Routing/` and `Requests/` hold the router and request types), `Forgejo/`, `Workspace/` (git, change checks), `Project/` (`project.json` parsing, rules, limits)
- `services/sandbox-launcher`, `services/egress-proxy`: run agents in disposable containers; limit what they can reach
- `libs/DevFoundry.Contracts`: types shared between services
- `agents/`: the role files (`agents/agents/*.md`), the baseline `opencode.json`, and `standards/` (texts copied into roles)
- `sandboxes/`, `egress/`, `infra/`: images, allowlist profiles, Compose stack
- `integrations/`: the roles and flows packaged for other harnesses (copies, see below)
- `docs/`: documentation with diagrams (`docs/diagrams/*.dot` render to `docs/images/*.svg`)

## Commands (from the repository root)
- Unit tests of a service: `docker build --target test -f services/<service>/Dockerfile -t t . && docker run --rm t`
- Controller, fast tests only (no Temporal dev server, under a minute): `docker run --rm -e TEST_FILTER='Category!=Scenario' t`
- Controller, only the workflow scenarios: `docker run --rm -e TEST_FILTER='Category=Scenario' t`
- Documentation, links, settings and copies: `python3 tools/check-docs.py`
- Redraw diagrams after editing a `.dot` file: `sh docs/diagrams/render.sh`
- Rebuild and restart a service: `cd infra && docker compose up -d --build <service>`

## Rules that keep it correct
- **The controller decides, agents advise.** An agent returns one validated JSON verdict; code decides what happens. Do not move a decision into a prompt.
- **A new `project.json` key** needs parsing and tests in `Project/`, an entry in `docs/project-setup.md`, and (if it has a factory default) a `FACTORY_DEFAULT_*` setting in `WorkerSettings`, `infra/compose.yaml`, `infra/.env.example` and `docs/configuration.md`. `check-docs.py` fails when one is missing.
- **A new environment variable** must be documented in `docs/configuration.md`.
- **A new agent role** needs a file in `agents/agents/`, a row in `docs/configuration.md`, and a prompt builder and parser in `Agents/`. Put its limits in the role file next to its answer shape.
- **Shared role texts** (`agents/standards/engineering-floor.md`, `security-checklist.md`) are copied into role files between markers. Edit the source, paste it into the roles, and let `check-docs.py` confirm they match.
- **`integrations/`** holds copies of the role files. Update them when a role changes.
- **Tests:** workflow scenarios live in `Scenarios/` and use scripted agents with fake activities (`Harness.cs`). Tag any new test that needs the Temporal dev server with `[Trait("Category", "Scenario")]` and the `temporal` collection.
- **Temporal determinism:** workflow code must not read the clock, environment or files directly. Read them in an activity, so a replayed run sees the same value.
- Never commit `infra/.env` or anything with credentials.
