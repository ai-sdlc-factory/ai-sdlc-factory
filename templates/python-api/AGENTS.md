# Greetings API (Python)

## What this is
A tiny web API that greets a person by name. It exists to show how a Python project looks when the factory works on it. Replace this section with what your project really is.

## Layout
- `greetings/greeting.py`: the business rule (pure functions)
- `greetings/web.py`: the web layer, a plain WSGI application with no dependencies
- `tests/`: pytest tests for both
- `e2e/`: end-to-end tests against the running API, written by the `/qa` run (`pytest.ini` does not collect them)
- `pyproject.toml`: project metadata and dependencies. `ruff.toml` and `pytest.ini`: the lint and test settings

## Build and test
- Lint: `ruff check .`
- Test: `python -m pytest -q`
- Run: `python -m greetings.web` (listens on http://127.0.0.1:5080)
The factory runs the commands under `gates` in `.factory/project.json`. If this file and the gates disagree, the gates win.

## Conventions
- Python 3.12, type hints on public functions, no wildcard imports.
- Business rules are pure functions in their own module; the web layer only translates to and from HTTP.
- Bad input is a `400` with a short JSON `{"error": "..."}`; never return `200` for a failure.
- Use the `logging` module, not `print`. Never log personal data.
- Keep to the standard library unless the ticket needs a package; say why in the pull request.

## Testing
- pytest, plain `assert`. One behaviour per test, named after what happens.
- Every change in behaviour comes with a test. A bug fix includes a test that fails without the fix.

## Do not touch
- `e2e/`: the end-to-end tests belong to the `/qa` run, and only it may change them. If one fails, fix the application, not the test.
- `ruff.toml` and `pytest.ini` (the lint and test settings): a person changes these, so a check cannot be loosened to pass.
- A new dependency is fine when the ticket needs it (it goes in `pyproject.toml`); say why in the pull request.
