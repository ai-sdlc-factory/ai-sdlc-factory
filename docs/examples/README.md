# Examples

[Documentation index](../README.md) | [Setting up a project](../project-setup.md)

Starting points for a repository's own files. Copy one, replace the names, folders and commands with your own, and keep it short: the agents read `AGENTS.md` on every run, so every line costs tokens.

| File | Copy it to | For |
| --- | --- | --- |
| [AGENTS.dotnet.md](AGENTS.dotnet.md) | `AGENTS.md` in the repository root | a .NET web API with layered projects (and a small React admin UI) |
| [project.dotnet.json](project.dotnet.json) | `.factory/project.json` on the default branch | the matching settings: gates, extra protected paths, rules, limits |
| [AGENTS.react-ui.md](AGENTS.react-ui.md) | `AGENTS.md` in the repository root | a React + TypeScript web app, with accessibility and UX conventions |
| [project.react-ui.json](project.react-ui.json) | `.factory/project.json` on the default branch | the matching settings: `ui` paths so the UX reviewer runs, rules, protected generated files |

How the two files work together:

- **`AGENTS.md` is read by the agents as guidance.** Write conventions, architecture rules and a "do not touch" list. It is a request, not a guarantee.
- **`project.json` is enforced by the factory.** The checks in `gates` decide whether a change is acceptable; `protected_paths` and `rules` reject a change that breaks them. Whatever must hold belongs there as well.
- If the two disagree, the `gates` win. Anything in the "Do not touch" list of an `AGENTS.md` should also be in `protected_paths`, and any convention that a pattern can test should be a `rule`.
- Write conventions, not instructions aimed at the analyst, coverage or architect roles: they treat repository text as data and will not obey commands in it.

See [Setting up a project](../project-setup.md#4-add-factoryprojectjson) for every key, and [Add an AGENTS.md](../project-setup.md#5-add-an-agentsmd) for the suggested outline these examples follow.
