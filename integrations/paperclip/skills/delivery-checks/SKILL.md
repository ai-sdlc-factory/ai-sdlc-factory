---
name: delivery-checks
description: The checks done in code, not in prompts: protected paths, secrets, red-then-green, evidence, bounded loops
---
These are what make dev-foundry's output trustworthy. A prompt asks a model to behave; a check makes it unable to do otherwise. Reproduce them as scripts, hooks or CI steps in your harness. Nothing below needs a model.

## On every change an agent makes (after each developer round)

| Check | Rule |
| --- | --- |
| Protected paths | reject any change to the pipeline's own config, CI files, `.git/`, plus any extra patterns the project lists. Matching ignores case |
| Symbolic links | reject new symlinks, and files reached through one |
| Size | reject more than 300 changed files, a file over 2 MiB, or more than 10 MiB in total (usually build output) |
| Secrets | scan added files for credential shapes (keys, tokens, `password = "..."`) and reject them |
| Noise | `.gitignore` decides what is build output; a change that adds `node_modules`, `.venv`, `bin/`, `obj/` is rejected |
| Project rules | optional regex rules on **added lines** in named files (for example "no `BinaryFormatter`"); `block` sends the change back, `warn` goes to the reviewers |
| Project checks | run the project's own commands (build, tests, lint, audit) in a fresh environment **without model access**; the exit code decides; feed the end of the output back to the developer |

## Between stages

| Check | Rule |
| --- | --- |
| Answer shape | parse the last JSON object; check required fields, limits and allowed values; one retry that quotes the exact problem, then stop and ask a person |
| Verdict agrees with data | `needs_changes` iff a blocker or major finding; `fully_covered` only if all `covered`; `pass` needs at least one criterion, etc. |
| Evidence exists | every file an analyst, QA or investigator cites must exist (and every commit); drop what does not and weaken the claim (`covered`→`partial`→`missing`; `met`→`not_met`; hypothesis→"guess") |
| Triage by counting | choose "needs a design" and "needs a UX review" from numbers (files estimated, API or data model change, UI files and lines changed), not by asking a model |
| Bounded loops | at most 3 build rounds, 3 rounds of questions per stage, 3 revisions per pull request (all configurable) |
| Nothing unreviewed | if a review or QA answer stays unusable after the retry, **stop**; never publish unreviewed |

## Bug fixes: red then green

1. Run the reproducer's test command on the **unchanged** code. It must **fail** (a pass, or "command not found", goes back to the reproducer).
2. Hash the reproducer's test files. The developer may not change them: compare the hashes after the developer round.
3. After the developer round, the same command must **pass**, and the project checks must pass.

## Review comments (pr-resolver)

- The number of responses equals the number of comments; each index appears exactly once.
- `fixed` requires changed files; if nothing changed, the answer goes back.
- Push as a normal (never forced) commit onto the branch; if the branch moved, rebase once, and give up on a conflict.

## Incidents

- No production access for any agent. The only facts are in the ticket.
- Humans decide with fixed commands (`/option N`, `/hotfix`, `/resolved`), not by a model reading free text.
- The postmortem writer may create exactly one new file; reject anything else.

## Publishing

Commit and push from a **separate, trusted checkout**: copy only the files that passed the checks from the agent's working copy onto a pristine clone, commit there, push. The agent never holds the push credential and never runs `git commit`. The pull request is opened by the pipeline and always needs a person's review; the pipeline never merges.
