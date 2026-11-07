# Dev Foundry

An AI development factory built around Forgejo, Temporal and disposable agent containers. You label a Forgejo ticket; role agents analyse it, write the code (or the fix, or the
incident advice) in isolated sandboxes, and the factory hands you a **reviewed pull request**. A person reviews and merges; the factory never merges.

![System overview](docs/images/architecture.svg)

## What it does

| Label the ticket | You get |
| --- | --- |
| `feature` | analysis, a check that it is not already built, an optional design review, the implementation, the project's own checks, code/security/UX review and QA, then a pull request |
| `bug` | a pull request with a fix **proven by a test that failed before and passes after**; the factory runs that proof itself |
| `incident` | an assessment, ranked likely causes (every cited file and commit is verified) and mitigation options, driven by `/option`, `/hotfix` and `/resolved`; a draft postmortem on request. It never touches production |

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
| [Workflows](docs/workflows.md) | Routing, the feature, bug and incident workflows, quick mode, statuses, limits (with diagrams) |
| [Architecture](docs/architecture.md) | The parts, trust boundaries, where data lives |
| [Configuration](docs/configuration.md) | Every setting: `.env`, service variables, agents and models |
| [Operations](docs/operations.md) | Watching runs, troubleshooting, cleaning up, backups, security notes |
| [Design and decisions](docs/design.md) | Why it is built this way, findings from running it, what is not built yet |
| [Infrastructure](infra/README.md) | Compose services, local addresses, the bot account |
| [Agents](agents/README.md) | The role files, permissions and models |
| [OpenCode login](docs/opencode-login.md) | Signing the agents in with a ChatGPT account |

## Repository

- [`infra/`](infra/README.md): Docker Compose and environment settings.
- [`services/api/`](services/api/README.md): webhook intake. [`services/controller/`](services/controller/README.md): workflows, agent prompts and parsers, workspace and Forgejo handling.
  [`services/sandbox-launcher/`](services/sandbox-launcher/README.md): the only service with Docker access. [`services/egress-proxy/`](services/egress-proxy/README.md): the proxy that decides what sandboxes may reach.
- [`agents/`](agents/README.md): one Markdown file per agent role, plus shared OpenCode settings.
- [`sandboxes/`](services/sandbox-launcher/README.md#images): Dockerfiles for the images agents run in (`dotnet`, `python`, `node`, and the combined `dotnet-node`, `python-node`, `full`).
- [`egress/`](egress/README.md): the internet allowlist for the sandboxes.
- [`libs/DevFoundry.Contracts/`](libs/DevFoundry.Contracts/Contracts.cs): wire types shared by the services.
- [`tests/integration/`](tests/integration/README.md): the deployed pipeline test. [`tools/check-docs.py`](tools/check-docs.py): the documentation check.
- [`bitbucket-pipelines.yml`](bitbucket-pipelines.yml): CI running the service tests, a vulnerable-package check, the documentation check and the integration tests.

All services are C# on .NET 10; open [dev-foundry.slnx](dev-foundry.slnx) in your IDE. Build settings live in [`Directory.Build.props`](Directory.Build.props) and NuGet versions in
[`Directory.Packages.props`](Directory.Packages.props); change a package version there, not in a project file.
