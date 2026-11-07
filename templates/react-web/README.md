# Template: React web app

A minimal Vite + React + TypeScript app with component tests, ready for the factory. It type-checks, builds and its tests pass.

```bash
npm ci               # install exactly what package-lock.json says
npm test             # run the tests
npm run dev          # http://127.0.0.1:5173
npm run build        # production build into dist/
```

## What the factory settings turn on

[`.factory/project.json`](.factory/project.json) uses every project setting that applies to a front-end project:

| Setting | Here | What it does |
| --- | --- | --- |
| `image` | `node` | the sandbox image every agent and check runs in (Node 22, npm) |
| `mode` | `full` | the full process; `"quick"` would skip the coverage check, design review and QA |
| `record` | `true` | commits a run record to `.factory/runs/` on each pull request |
| `reviewers` | `reviewer`, `security` | who reviews every change besides QA |
| `ui` | `src/**/*.tsx`, `*.css`, `index.html` | declares a user interface, so the **UX reviewer** runs on a UI change that is medium (3 files or 40 lines) or large (8 files or 200 lines) |
| `limits` | 3 / 3 / 3 | build rounds, rounds of questions to a person, revisions per pull request |
| `protected_paths` | `vite.config.ts`, `tsconfig.json` | files no agent may change. The lockfile is not protected: a new dependency changes `package.json` and `package-lock.json` together, and the `install` check (`npm ci`) fails if they disagree |
| `rules` | 4 | patterns a change may not add: two block the change, two only warn the reviewers |
| `gates` | install, typecheck, test, build | the checks that decide whether a change is acceptable |

Not used here: a gate `cwd` (the app is at the repository root). See the `dotnet-react` template.

## Use it

See [../README.md](../README.md#use-a-template).
