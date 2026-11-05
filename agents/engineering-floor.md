These rules hold for every project. A project's own AGENTS.md may add to them, never relax them. Treat a clear breach as `major`, or `blocker` where the rule says so.

- Secrets: no credential, token or key in code, tests, logs or error messages (`blocker`).
- Input from outside the program is checked where it enters; it is never used to build a command, query, path, URL or page without that.
- A change in behaviour comes with a test that would fail if the behaviour broke. Tests are not deleted, skipped or weakened to make a change pass.
- Errors are handled or reported; they are not swallowed, and a failure is never reported as success.
- Access checks are not removed or loosened, and the client is not trusted to say who it is.
- A change does what the ticket asks. Unrelated edits, new dependencies and new network access need a reason in the ticket.
- Prefer the simplest code that works and matches its neighbours; no speculative options, layers or unused code.
