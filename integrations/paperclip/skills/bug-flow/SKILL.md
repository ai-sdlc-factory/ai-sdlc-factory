---
name: bug-flow
description: Flow for a bug fix proven by a test that fails before and passes after
---
Goal: a fix proven by a test that fails before and passes after. Roles: `bug-analyst`, `reproducer`, `diagnostician`, `developer`, `reviewer`, `security`.

| # | Step | Role / actor | What the code checks | Next step by result |
| --- | --- | --- | --- | --- |
| 1 | Analyse | `bug-analyst` | answer shape | `pass` → 2; `needs_clarification` → ask, wait, repeat; `reject` (not a bug) → **stop** |
| 2 | Reproduce | `reproducer` | only test files changed, no deletions, protected paths and secrets as usual | `reproduced` → 3; `cannot_reproduce` → ask or **stop** |
| 3 | **RED check** | code | run the reproducer's test command on the **unchanged** code: it must **fail**. A pass, a missing command (exit 126/127) or a timeout goes back to step 2 with the reason | up to 3 attempts, then **stop** (cannot reproduce) |
| 4 | Diagnose | `diagnostician` | answer shape | `pass` → 5; `too_large` → **stop**, suggest a feature ticket; `needs_clarification` → ask |
| 5 | Fix | `developer` | gets the failing test, the failure output and the root cause; may not edit the test | build round as in `feature.md` |
| 6 | **GREEN check** | code | the hashes of the test files are unchanged, the project's checks pass, and **the same test command now passes** | any failure → next round |
| 7 | Review | `reviewer`, `security` | as in `feature.md` | blocker/major → next round |
| 8 | Publish | code | | pull request titled "Fix: ..." with severity, root cause, the test, and a statement that it failed before and passes after |

QA and the architect are not used: red then green is the proof. Quick mode skips only step 4.
