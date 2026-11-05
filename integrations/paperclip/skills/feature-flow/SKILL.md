---
name: feature-flow
description: Step-by-step flow for turning a feature request into a reviewed change, with the verdict table and the checks to run
---
Goal: turn a feature request into a reviewed change. Roles: `analyst`, `coverage`, `architect`, `developer`, `reviewer`, `security`, `ux`, `qa`. See `checks.md` for every "check" below.

| # | Step | Role / actor | Input | Next step by result |
| --- | --- | --- | --- | --- |
| 1 | Intake | code | repository, ticket | create a working copy and a branch; read the project settings (image/tooling, check commands, which reviewers). No usable settings: **stop** and say what to add |
| 2 | Analyse | `analyst` | ticket | `pass` → 3; `needs_clarification` → ask the questions, wait, repeat (at most 3 rounds of questions; the last run must decide); `reject` → **stop** |
| 3 | Coverage | `coverage` + **check** | the criteria from step 2 | check that every cited file exists and weaken claims that fail; all `covered` → **stop** (already implemented); otherwise keep only the `partial` and `missing` criteria → 4 |
| 4 | Triage | **code, no model** | analyst's complexity facts, labels | needs a design if any: more than 5 files estimated, more than one project, public API change, data model change, new dependency, or the ticket asks for it → 5; otherwise → 6 |
| 5 | Design | `architect` | remaining criteria, reasons from step 4 | `pass` → 6; `needs_decision` → ask, wait, repeat (bounded); `reject` → **stop** |
| 6 | Build round (repeat up to 3 times) | `developer` | ticket, remaining criteria, design, check commands, feedback from the last round | see below |
| 7 | Publish | code | the verified files | commit and push from a trusted checkout, open a pull request that lists the criteria, the design, the check results and the reviewers' notes; a person reviews it |

## Build round

1. Run `developer`. `blocked` → **stop** with its reasons. Nothing changed → feedback "no files changed", next round.
2. **Change checks** (protected paths, symlinks, size, secrets, noise). A violation → feedback, next round.
3. **Project checks** (build, tests, lint, audit) and **project rules**. A failure → feedback with the end of the output, next round.
4. **Reviewers** in parallel if you can: `reviewer`, plus `security` and others the project lists, plus `ux` only if the project has a UI and the UI change is medium or large (decide by counting changed UI files and lines). A blocker or major finding → feedback, next round.
5. **QA** on the remaining criteria (check that cited files exist). Any `not_met` → feedback, next round. Skip in quick mode.
6. Everything passed → step 7. Three rounds without success → **stop**, save the work on a work-in-progress branch for a person, say what failed last.

A review or QA answer that stays unusable after one retry **stops** the flow. Nothing is published unreviewed.

## Quick mode

For small, low-risk changes: skip coverage (step 3), design (step 5) and QA. Keep everything else, including the security review. Only a person chooses it (a label or a project setting), never an agent.
