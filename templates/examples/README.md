# Examples

[Documentation index](../../docs/README.md) | [Setting up a project](../../docs/project-setup.md) | [Templates](../README.md)

Starting points for a repository's own files. Copy one, replace the names, folders and commands with your own, and keep it short: the agents read `AGENTS.md` on every run, so every line costs tokens.

| File | Copy it to | For |
| --- | --- | --- |
| [AGENTS.dotnet.md](AGENTS.dotnet.md) | `AGENTS.md` in the repository root | a .NET web API with layered projects (and a small React admin UI) |
| [project.dotnet.json](project.dotnet.json) | `.factory/project.json` on the default branch | the matching settings: gates, extra protected paths, rules, limits, and `verify` (an `api` suite for `/qa`) |
| [AGENTS.react-ui.md](AGENTS.react-ui.md) | `AGENTS.md` in the repository root | a React + TypeScript web app, with accessibility and UX conventions |
| [project.react-ui.json](project.react-ui.json) | `.factory/project.json` on the default branch | the matching settings: `ui` paths so the UX reviewer runs, rules, protected generated files, and `verify` (a Playwright `ui` suite for `/qa`, which starts the app itself) |
| [Dockerfile.dotnet-redis](Dockerfile.dotnet-redis) | `.factory/Dockerfile` on the default branch | a project's own sandbox image: a standard image plus `redis-server`. Needs `FACTORY_ALLOW_REPO_IMAGES=true`; [read the rules](../../docs/project-setup.md#a-projects-own-image) first |

How the two files work together:

- **`AGENTS.md` is read by the agents as guidance.** Write conventions, architecture rules and a "do not touch" list. It is a request, not a guarantee.
- **`project.json` is enforced by the factory.** The checks in `gates` decide whether a change is acceptable; `protected_paths` and `rules` reject a change that breaks them. Whatever must hold belongs there as well.
- If the two disagree, the `gates` win. Anything in the "Do not touch" list of an `AGENTS.md` should also be in `protected_paths`, and any convention that a pattern can test should be a `rule`.
- Write conventions, not instructions aimed at the analyst, coverage or architect roles: they treat repository text as data and will not obey commands in it.

See [Setting up a project](../../docs/project-setup.md#4-add-factoryprojectjson) for every key, and [Add an AGENTS.md](../../docs/project-setup.md#5-add-an-agentsmd) for the suggested outline these examples follow.
