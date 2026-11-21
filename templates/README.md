# Templates

Small, working starter projects with **every factory feature switched on**, to show what a repository looks like when the factory works on it. Each one builds, passes its own tests in the factory's sandbox image, and has the files the factory reads. They are deliberately tiny: the code is a stand-in you replace.

| Template | What it is | Sandbox image | Shows |
| --- | --- | --- | --- |
| [dotnet-api](dotnet-api/) | an ASP.NET Core web API with xUnit tests | `dotnet` | a back-end project: gates, rules, protected paths, limits, run record, security review |
| [python-api](python-api/) | a Python web API (standard library only) with pytest and ruff | `python` | the same for Python, with a lint gate |
| [react-web](react-web/) | a Vite + React + TypeScript app with Vitest | `node` | a front-end project: the `ui` setting that brings in the UX reviewer, `npm ci` gates |
| [dotnet-react](dotnet-react/) | a .NET API and a React front end in one repository | `dotnet-node` | a project with two parts: a combined image, gates that run in a subfolder (`cwd`), and an example of a project's own `Dockerfile` |

Besides the starter projects, [examples/](examples/README.md) holds loose files to copy into your own repository: ready-made `AGENTS.md` and `project.json` files for a .NET and a React project, and a project's own `Dockerfile`.

## What is in each one

| File | What it is for |
| --- | --- |
| `.factory/project.json` | the factory's settings: image, checks, reviewers, limits, rules. **Required**; only a person edits it |
| `.factory/instructions.md` | the project's house rules for the agents, with a short section per role (optional; read from the default branch and added to every prompt) |
| `AGENTS.md` | what the agents should know about the project: layout, commands, conventions, what not to touch |
| `.gitignore` | keeps build output out of changes (the factory rejects a change that adds it) |
| `README.md` | how to run the project and what each setting does |
| `e2e/` | end-to-end tests for `/qa`, run against the started application (the `verify` setting). They are not part of the gates |
| the code and tests | a stand-in application that builds and passes, so the checks have something to check |

The settings are explained in [Setting up a project](../docs/project-setup.md); the `AGENTS.md` files follow the outline there, and the `instructions.md` files show [project instructions](../docs/project-setup.md#project-instructions-factoryinstructionsmd).

## Use a template

1. **Copy the folder** into a new directory and make it a repository:
   ```bash
   cp -r templates/dotnet-api ~/projects/my-api && cd ~/projects/my-api
   git init -b main && git add -A && git commit -m "Start from the dotnet-api template"
   ```
2. **Put it in Forgejo**: create an empty repository there, add it as `origin` and push `main`. The factory needs at least one commit on the default branch, and `.factory/project.json` on it.
3. **Connect it to the factory**, once per repository, as in [Setting up a project](../docs/project-setup.md): give the `factory-bot` account **Write** access, add the webhook, and create the labels (`feature`, `bug`, `incident`, and optionally `quick`, `full`, `needs-architecture`).
4. **Build the sandbox image** the template names, if you have not yet (`docker compose --profile images build sandbox-image-<name>` in `infra/`; the combined `dotnet-node` is needed by `dotnet-react`).
5. **Make it yours**: replace the stand-in code, the "What this is" part of `AGENTS.md` and the example rules in `.factory/instructions.md` (or delete that file), and adjust `.factory/project.json` (check commands, rules, limits). Then open a ticket with the `feature` label.

## Notes

- **The checks run in the sandbox without Docker or any service**, so the templates' tests use nothing outside the code. Yours should do the same, or use in-memory fakes.
- **`record: true`** puts a run record (every prompt, answer and check output) into each pull request under `.factory/runs/`. Set it to `false` if you do not want that.
- **A project's own image** is off unless the operator sets `FACTORY_ALLOW_REPO_IMAGES=true`. `dotnet-react/.factory/Dockerfile.example` shows what one looks like; it does nothing under that name.
- **Checks must not write files that git would show.** That is why the front-end templates commit `package-lock.json` and install with `npm ci` (an `npm install` would create the lockfile during every run, the reviewers would flag it as unrelated, and the developer could not remove it). Every template's checks were run on a clean copy and left nothing behind. See "What a good gate is" in [Setting up a project](../docs/project-setup.md).
- **The front-end tests have long timeouts on purpose** (`testTimeout` and `asyncUtilTimeout` in `vite.config.ts` and `src/test-setup.ts`). On a machine whose clock steps by several seconds (WSL2 can), the default 5 s limit fires in tests that take milliseconds. A real hang still fails, after 30 s. Details in [Operations](../docs/operations.md#troubleshooting).
- Versions of the packages are pinned (`package-lock.json`, explicit NuGet versions) so a run is repeatable. **Dependencies are not protected paths**: a ticket may need a new package, and then the manifest and its lockfile change together. Protecting one without the other makes that impossible (`npm ci` fails when they disagree); to forbid new dependencies altogether, protect both. What the templates protect instead is what would let a check be weakened or the toolchain change: migrations, the SDK pin, lint and bundler settings.
