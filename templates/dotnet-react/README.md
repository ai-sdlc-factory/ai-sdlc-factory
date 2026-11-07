# Template: .NET API + React web app

A .NET API and a React front end in one repository, with tests for both, ready for the factory. Everything builds and its tests pass (9 API tests, 6 web tests).

```bash
dotnet test                                   # API tests
dotnet run --project src/Greetings.Api        # http://localhost:5080/api/greetings/Ada
cd frontend && npm ci && npm test             # web tests
cd frontend && npm run dev                    # http://127.0.0.1:5173 (forwards /api to the API)
```

## What the factory settings turn on

[`.factory/project.json`](.factory/project.json) uses **every** project setting:

| Setting | Here | What it does |
| --- | --- | --- |
| `image` | `dotnet-node` | one sandbox image with both the .NET SDK and Node, so every check can run |
| `mode` | `full` | the full process; `"quick"` would skip the coverage check, design review and QA |
| `record` | `true` | commits a run record to `.factory/runs/` on each pull request |
| `reviewers` | `reviewer`, `security` | who reviews every change besides QA |
| `ui` | `frontend/src/**/*.tsx`, `*.css`, `index.html` | declares a user interface, so the **UX reviewer** runs on a UI change that is medium or large |
| `limits` | 3 / 3 / 3 | build rounds, rounds of questions to a person, revisions per pull request |
| `protected_paths` | migrations, `global.json`, `vite.config.ts` | files no agent may change. Dependencies are not protected: `package.json` and its lockfile change together, and the web checks use `npm ci`, which fails if they disagree |
| `rules` | 4 | patterns a change may not add: three block the change, one only warns the reviewers |
| `gates` | 2 for the API, 4 for the web | the checks that decide whether a change is acceptable. The web gates use `cwd: "frontend"` to run inside that folder |

Also here: [`.factory/Dockerfile.example`](.factory/Dockerfile.example), a project's own sandbox image (adds a Redis server). It is inactive under that name: see the comments in it, and "A project's own image" in `docs/project-setup.md`, before you rename it.

## Use it

See [../README.md](../README.md#use-a-template).
