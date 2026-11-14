# Template: Python API

A minimal Python web API (standard library only) with tests and lint, ready for the factory. Lint is clean and its tests pass.

```bash
python -m pytest -q          # run the tests
ruff check .                 # lint
python -m greetings.web      # http://127.0.0.1:5080/api/greetings/Ada
```

## What the factory settings turn on

[`.factory/project.json`](.factory/project.json) uses every project setting that applies to a back-end project:

| Setting | Here | What it does |
| --- | --- | --- |
| `image` | `python` | the sandbox image every agent and check runs in (Python 3.12, pytest, ruff) |
| `mode` | `full` | the full process; `"quick"` would skip the coverage check, design review and QA |
| `record` | `true` | commits a run record to `.factory/runs/` on each pull request |
| `reviewers` | `reviewer`, `security` | who reviews every change besides QA |
| `limits` | 3 / 3 / 3 | build rounds, rounds of questions to a person, revisions per pull request |
| `protected_paths` | `ruff.toml`, `pytest.ini` | the lint and test settings, so no agent can loosen a check to make it pass. Dependencies are not protected |
| `rules` | 3 | patterns a change may not add: two block the change, one only warns the reviewers |
| `gates` | lint, test | the checks that decide whether a change is acceptable |
| `verify` | `e2e` | how `/qa` tries a finished change for real: an `api` suite in `e2e/` (pytest against the running API). Only used by a `/qa` run; `e2e` is protected for every other run |

Not used here: `ui` (no user interface) and a gate `cwd`.

## Use it

See [../README.md](../README.md#use-a-template).
