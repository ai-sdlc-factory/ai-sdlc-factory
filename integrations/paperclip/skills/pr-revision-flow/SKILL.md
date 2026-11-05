---
name: pr-revision-flow
description: Flow for addressing review comments on a pull request with one new commit
---
Goal: a person reviewed a pull request the pipeline opened and asked for changes; address the comments with **one new commit** on the same branch. Roles: `pr-resolver`, `reviewer`, `security`.

Start when a review requests changes, or a comment says `/fix` (optionally followed by an instruction).

| # | Step | Role / actor | Rule |
| --- | --- | --- | --- |
| 1 | Eligibility | code | the pull request is the pipeline's own (branch name, author), open, from the same repository, and has had fewer than 3 revisions; there is something to act on. Otherwise do nothing |
| 2 | Collect | code | every review still waiting (not dismissed, not already answered), plus the instruction after `/fix`; number the comments from 1; each line comment carries the code it was written on |
| 3 | Resolve | `pr-resolver` | works on the pull request's own branch; **exactly one response per comment** (`fixed`, `answered`, `declined`, `needs_clarification`); `fixed` requires changed files, else send the problem back |
| 4 | Checks | code | change checks, project checks, project rules (as in `checks.md`); up to 3 rounds |
| 5 | Review | `reviewer`, `security` | on the revision's diff; a larger revision (3+ files) is also checked against the acceptance criteria in the pull request body |
| 6 | Push | code | a normal push (never forced). If the branch moved, put the commit on top once; on a conflict, push nothing |
| 7 | Reply | code | one comment: the new commit, a table of what was done for each comment, the check results; ask the reviewer to look again. New feedback that arrived meanwhile is handled as a follow-up revision afterwards |

If every comment was answered or declined and no files changed, push nothing and reply with the answers.
