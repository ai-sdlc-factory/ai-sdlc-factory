# Dev Foundry

An AI development factory built around Forgejo, Temporal and disposable agent containers. You label a Forgejo ticket; role agents analyse it, write the code (or the fix, or the
incident advice) in isolated sandboxes, and the factory hands you a **reviewed pull request**. A person reviews and merges; the factory never merges.

![System overview](docs/images/architecture.svg)

## What it does

| Label the ticket | You get |
| --- | --- |
| `feature` | analysis, a check that it is not already built, an optional design review, the implementation, the project's own checks, code/security/UX review and an acceptance check, then a pull request |
| `bug` | a pull request with a fix **proven by a test that failed before and passes after**; the factory runs that proof itself |
| `docs` | a pull request that writes or updates `AGENTS.md`, `README.md` and `docs/` from the code, checked by the reviewers |
| `incident` | an assessment, ranked likely causes (every cited file and commit is verified) and mitigation options, driven by `/option`, `/hotfix` and `/resolved`; a draft postmortem on request. It never touches production |

On top of the labels, people steer with comments on a ticket or pull request: `/fix` (revise the pull request), `/update` (merge the base branch into it, resolving conflicts), `/review` (have the reviewers read any pull request and post findings), `/retry` (run a ticket again from the last good stage, or `/retry restart` from the beginning), `/qa` (test the running application, with `/qa full` and `/fix qa`), and `/option`, `/hotfix` and `/resolved` on incidents. A [demo "production"](demo/README.md) with Prometheus, Grafana and Loki shows the incident side: an alert opens a ticket by itself and the factory starts on it.

Everything is deterministic where it matters: agents only advise with one validated JSON verdict, and code decides every next step. Each agent runs in a throwaway container with no route to the
internet except an allowlisting proxy. See [Architecture](docs/architecture.md) and [Workflows](docs/workflows.md).

## Quick start

1. [Set up the factory](docs/getting-started.md): configure `infra/.env`, `docker compose up -d --build`, build the sandbox images, create the bot account, sign the agents in. About 30 minutes, once.
2. [Connect a repository](docs/project-setup.md): bot access, a webhook, labels, and a small `.factory/project.json`. About 5 minutes per repository.
3. Create a ticket, add the `feature` label, and watch the comments appear.

## Documentation

Start at the [documentation index](docs/README.md).

| Guide | Covers |
| --- | --- |
| [Getting started](docs/getting-started.md) | Installing and configuring the factory, step by step |
| [Setting up a project](docs/project-setup.md) | Webhook, labels, `.factory/project.json` (required and optional settings), `AGENTS.md` |
| [Workflows](docs/workflows.md) | Routing, the feature, bug, incident and plan workflows, quick mode, statuses, limits (with diagrams) |
| [Architecture](docs/architecture.md) | The parts, trust boundaries, where data lives |
| [Configuration](docs/configuration.md) | Every setting: `.env`, service variables, agents and models |
| [Operations](docs/operations.md) | Watching runs, troubleshooting, cleaning up, backups, security notes |
| [Design and decisions](docs/design.md) | Why it is built this way, findings from running it, what is not built yet |
| [Infrastructure](infra/README.md) | Compose services, local addresses, the bot account |
| [Demo and monitoring](demo/README.md) | A demo production for the weather app with Prometheus, Alertmanager, Grafana and Loki, where alerts open tickets |
| [Agents](agents/README.md) | The role files, permissions and models |
| [OpenCode login](docs/opencode-login.md) | Signing the agents in with a ChatGPT account |

## Repository

- [`infra/`](infra/README.md): Docker Compose and environment settings.
- [`services/api/`](services/api/README.md): webhook intake. [`services/controller/`](services/controller/README.md): workflows, agent prompts and parsers, workspace and Forgejo handling.
  [`services/sandbox-launcher/`](services/sandbox-launcher/README.md): the only service with Docker access. [`services/egress-proxy/`](services/egress-proxy/README.md): the proxy that decides what sandboxes may reach.
- [`services/alert-intake/`](services/alert-intake/README.md): turns monitoring alerts into tickets. [`demo/`](demo/README.md): a demo production with monitoring, and the scripts to set it up, deliver code to it and break it on purpose.
- [`agents/`](agents/README.md): one Markdown file per agent role, plus shared OpenCode settings.
- [`templates/`](templates/README.md): working starter projects (.NET, Python, React, .NET + React) to copy, with every feature switched on.
- [`sandboxes/`](services/sandbox-launcher/README.md#images): Dockerfiles for the images agents run in (`dotnet`, `python`, `node`, and the combined `dotnet-node`, `python-node`, `full`, plus `browser` for end-to-end UI tests).
- [`egress/`](egress/README.md): the internet allowlist for the sandboxes.
- [`libs/DevFoundry.Contracts/`](libs/DevFoundry.Contracts/Contracts.cs): wire types shared by the services.
- [`tests/integration/`](tests/integration/README.md): the deployed pipeline test. [`tools/check-docs.py`](tools/check-docs.py): the documentation check.
- [`bitbucket-pipelines.yml`](bitbucket-pipelines.yml): CI running the service tests, a vulnerable-package check, the documentation check and the integration tests.

All services are C# on .NET 10; open [dev-foundry.slnx](dev-foundry.slnx) in your IDE. Build settings live in [`Directory.Build.props`](Directory.Build.props) and NuGet versions in
[`Directory.Packages.props`](Directory.Packages.props); change a package version there, not in a project file.
