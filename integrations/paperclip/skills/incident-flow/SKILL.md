---
name: incident-flow
description: Advice-only flow for an incident: assessment, likely causes, mitigation options, postmortem
---
Goal: an assessment, likely causes and mitigation options for **people** to act on. **No agent has access to production.** The ticket is the only source of facts. Roles: `incident-analyst`, `investigator`, `mitigator`, `postmortem-writer`.

| # | Step | Role / actor | What the code checks | Next step |
| --- | --- | --- | --- | --- |
| 1 | Assess | `incident-analyst` | answer shape | `pass` → 2; `needs_clarification` → ask, wait; `reject` (not an incident here) → **stop** |
| 2 | Investigate | `investigator` (read, plus `git log/show/blame/diff`) | every cited file and commit exists; drop what does not; a hypothesis left with nothing to point at is shown as a **guess** | → 3 |
| 3 | Options | `mitigator` | 1 to 4 options, valid `recommended` | → 4 |
| 4 | People decide | **fixed commands**, not a model reading free text | | see below |

Commands a person writes on their own line in a comment:

| Command | Effect |
| --- | --- |
| `/option N` | post the checklist for option N; a person carries it out |
| `/hotfix` | open **one** bug ticket with the suspected cause; the bug flow takes over |
| `/resolved` | run `postmortem-writer` on the ticket record; accept exactly one new file (`docs/postmortems/<date>-incident-<n>.md`); open a pull request |
| anything else | new information: run steps 2 and 3 again (at most 3 times, then tell people to use a command or close the ticket) |

The flow ends when the ticket is closed or after 7 days without a reply.
