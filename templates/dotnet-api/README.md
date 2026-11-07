# Template: .NET API

A minimal ASP.NET Core web API with tests, ready for the factory. It builds with no warnings and its tests pass.

```bash
dotnet test                              # build and run the tests
dotnet run --project src/Greetings.Api   # http://localhost:5080/api/greetings/Ada
```

## What the factory settings turn on

[`.factory/project.json`](.factory/project.json) uses every project setting that applies to a back-end project:

| Setting | Here | What it does |
| --- | --- | --- |
| `image` | `dotnet` | the sandbox image every agent and check runs in |
| `mode` | `full` | the full process; `"quick"` would skip the coverage check, design review and QA |
| `record` | `true` | commits a run record (prompts, answers, check output, tokens) to `.factory/runs/` on each pull request |
| `reviewers` | `reviewer`, `security` | who reviews every change besides QA |
| `limits` | 3 / 3 / 3 | build rounds, rounds of questions to a person, revisions per pull request |
| `protected_paths` | migrations, `global.json` | files no agent may change. Dependencies are not protected: an agent may add a package, and the reviewers see it |
| `rules` | 3 | patterns a change may not add: two block the change, one only warns the reviewers |
| `gates` | build, test, audit | the checks that decide whether a change is acceptable |

Not used here: `ui` (there is no user interface) and a gate `cwd` (everything runs in the repository root). See the `dotnet-react` and `react-web` templates.

## Use it

See [../README.md](../README.md#use-a-template).
