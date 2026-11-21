# Commands and labels: a cheat sheet

[Documentation index](README.md) | [Workflows](workflows.md) | [Setting up a project](project-setup.md)

What to type, where to type it, and what starts. Everything happens on the git host: a **label** on a ticket starts a workflow, and a **comment** on a ticket or a pull request steers it. The factory never merges: a person reads and merges every pull request.

## I want to ...

| I want to ... | Do this | You get |
| --- | --- | --- |
| have a feature built | open a ticket with the label `feature` | a reviewed pull request |
| have a bug fixed | open a ticket with the label `bug` | a pull request with a fix proven by a test that failed before and passes after |
| split a big goal | open a ticket with the label `plan`, then `/approve` | proposed feature tickets, created when you approve |
| get advice on an incident | open a ticket with the label `incident` | an assessment, likely causes and mitigation options (nothing touches production) |
| have documentation written or updated | open a ticket with the label `docs` | a pull request with `AGENTS.md`, `README.md`, `CONTRIBUTING.md` and `docs/` changes |
| connect a repository that has no settings | comment `/setup` on any ticket | a pull request that adds `.factory/project.json`, with the checks that passed |
| ask a question about the code | comment `/ask <question>` on any ticket or pull request | an answer, as a comment (nothing is changed) |
| change the factory's pull request | submit a review that **requests changes**, or comment `/fix <what to change>` on the pull request | one new commit on the same branch |
| have any pull request reviewed | comment `/review` on it | the reviewers' findings, as comments (nothing is changed) |
| test a finished change for real | comment `/qa` on the ticket or the pull request | a QA report; end-to-end tests added to the pull request when they pass |
| bring a pull request up to date | comment `/update` on the ticket or the pull request | the base branch merged into it, conflicts resolved |
| run a ticket again | comment `/retry` on the ticket | the run carries on from the last good stage |

## Labels

A label on a ticket starts a workflow when the ticket is **opened**, **reopened** or **labelled**, and open. A ticket with no factory label is ignored (except for `/ask` and `/setup`). The operator can [add more names](configuration.md) for a kind (the host's `triggers` in `FACTORY_HOSTS`: `enhancement` for `feature`, say), map a host's work-item types, and turn on the **`/factory <kind>`** comment (`/factory feature`, `/factory bug`, ...), which asks for that workflow on an open ticket that carries no kind: it is off by default, because anyone who can comment could then start a run.

| Label | Starts | Produces |
| --- | --- | --- |
| `feature` | [feature workflow](workflows.md#feature-workflow) | a reviewed pull request |
| `bug` | [bug workflow](workflows.md#bug-workflow) | a pull request with a fix and the test that proves it |
| `plan` | [plan workflow](workflows.md#plan-workflow) | proposed tickets on the ticket; **nothing is created until you `/approve`** |
| `incident` | [incident workflow](workflows.md#incident-workflow) | assessment, causes and options on the ticket; a postmortem draft on `/resolved` |
| `docs` | [documentation run](workflows.md#documentation-run) | a pull request that writes or updates the documents |

Several labels on one ticket: **`incident` wins over `plan`, `plan` over `feature`, `feature` over `bug`, `bug` over `docs`**.

Labels that change how a run behaves (they start nothing by themselves):

| Label | Effect |
| --- | --- |
| `quick` | [quick mode](workflows.md#quick-mode): skips the coverage check, the design review and the acceptance check |
| `full` | the full process, even if the project defaults to quick |
| `needs-architecture` | forces a design review, even in quick mode |

Closing a ticket stops its run. Reopening it starts a new one. The labels must exist in the repository (`python3 tools/onboard-repo.py owner/repo` creates them, and the webhook).

## Commands

Write a command **on its own line** in a comment (upper or lower case). Text on the same or the following lines is an instruction or a question where the table says so. The factory ignores its own comments, so it never answers itself.

### On a ticket

| Command | What it does | Needs |
| --- | --- | --- |
| `/retry` | starts the ticket's run again, **carrying on from the stages that finished** (analysis, coverage check, design) | an open ticket with a factory label, no run in progress and **no open pull request** (a new run would open a second one: use `/fix` on the pull request, or `/retry restart`). If the ticket's text or labels changed, or the default branch has new commits that touch more than unrelated documents, it starts over and says why |
| `/retry restart` | starts again from the beginning (same as closing and reopening), even when the ticket already has an open pull request (the new run opens another one) | an open ticket with a run not in progress |

Closing a ticket stops its run, but only between two steps. If you reopen it before the run has stopped, the factory waits (up to about 30 minutes) and then starts the new run by itself.

### On a pull request, or on the ticket that has one

These work only on **the factory's own pull request** (branch `factory/issue-<n>`, authored by the bot, still open); on anyone else's pull request `/fix` and `/update` answer with a short note that they changed nothing. `/update` and `/qa` can also be typed on the **ticket**, and find the ticket's open pull request. `/fix` is typed on the **pull request**: on a ticket it is only a comment (it may be the answer to a question the factory asked there).

| Command | Type it on | What it does | Needs |
| --- | --- | --- | --- |
| `/fix` | the pull request | acts on every review still waiting. `/fix <instruction>` adds an instruction (`/fix rename the helper and add a test`); `/revise` is the same. One new commit, never forced, at most 3 revisions per pull request | something to act on: a review, or an instruction. A review that **requests changes** starts a revision by itself, without any command |
| `/fix qa` | the pull request | acts on the findings of the latest QA report | an earlier `/qa` report |
| `/update` | the pull request or the ticket | merges the base branch into the pull request's branch as one merge commit; resolves conflicts and checks the result | the line is only `/update`. A conflict in a protected file (`.factory/`, CI files) is left to a person |
| `/qa go` | the pull request or the ticket | the go-ahead for a QA run that waits for one because its estimate is over `limits.confirm_over` (off by default); says so when nothing waits | |
| `/qa` | the pull request or the ticket | tests the running application and writes end-to-end tests: `/qa` (this change), `/qa full` (the whole application), `/qa api` or `/qa ui` (one suite), plus a note (`/qa focus on the 404 cases`, up to 1,500 characters) | `verify` settings in `.factory/project.json`, merged into the pull request's branch |

### On any ticket or pull request

| Command | Type it on | What it does | Needs |
| --- | --- | --- | --- |
| `/ask <question>` | any ticket (no label needed) or any pull request | a read-only agent answers from the code and posts the answer. On a pull request it also sees what the pull request changes. Up to 1,500 characters; a bare `/ask` says how to use it | `.factory/project.json` in the repository |
| `/setup` | any ticket (no label needed) of a repository with **no** `.factory/project.json`; the comment must be only that line | the configurer proposes the settings, the factory runs every proposed check and opens a pull request with the ones that passed ([details](workflows.md#setting-up-a-repository-with-setup)) | a repository the bot can write to |
| `/review` | any pull request, **yours or a stranger's** | the reviewers read it and post findings. Reads the default branch's settings, so a fork cannot change what runs | an open pull request, and `.factory/project.json` |

### On a `plan` ticket

| Command | What it does |
| --- | --- |
| `/approve` | creates a `feature` ticket for every proposed ticket that waits for nothing (each starts the feature workflow and spends tokens) |
| `/approve 2 3` (or `2, 3`) | creates exactly those, including ones that wait for another |
| any other comment | a **change to the plan**: the planner plans again with your comment (3 times at most). Once any ticket is created the plan is final |

`/approve please` is not an approval: it is explained, and nothing changes.

### On an `incident` ticket

| Command | What it does |
| --- | --- |
| `/option N` (or `/choose N`) | posts the checklist for option N. A person carries it out; the factory has no access to production |
| `/hotfix` | opens **one** `bug` ticket with the suspected cause (needs a `bug` label in the repository) |
| `/resolved` | the incident is over: a postmortem is drafted from the ticket's record and opened as a pull request |
| any other comment | **new information**: the investigation and the options are looked at again (3 times at most) |

## Plain comments (no command)

| Where | What a plain comment does |
| --- | --- |
| a ticket whose workflow is waiting for you (the last comment asks a question) | **answers it**. Only the analyst, architect, reproducer, diagnostician, planner and incident stages read comments |
| a ticket whose build has started | gets one notice: it cannot change what is built |
| a ticket with no running workflow | gets a notice saying what to do (edit the ticket, then close and reopen it, or `/retry`) |

## Things that happen without a command

| What | Starts from |
| --- | --- |
| progress comments, the token budget and daily-limit notices, "the run did not finish, running it once more" | the run itself, as comments on the ticket |
| the weekly improvement report | a timer, once a week; with `FACTORY_IMPROVEMENT_REPO` set it opens a ticket with proposals (nothing is applied) |
| incident tickets from monitoring alerts | the [alert intake](../companions/alert-intake/README.md), in the [demo](../companions/demo/README.md) (alerts of one outage are grouped into one ticket) |

## Good to know

- **One at a time.** A second `/qa`, `/update`, `/review`, `/ask` or revision on the same ticket or pull request while one runs gets a notice; comment again afterwards. A `/fix` that arrives during a revision is queued and read by a follow-up.
- **By default anyone who can comment can use the commands.** The git host's repository permissions are the access control, unless the operator set a [`commands` rule](configuration.md#factory_hosts) for the host or the repository: write or admin access, or only named people. A person it refuses gets one short note (or nothing, if the rule says silent) and nothing runs. A label is not covered by the rule: the host limits who may label.
- **The webhook** must send **Issues**, **Issue Label**, **Issue Comment** and **Pull Request Review Rejected** events ([setup](project-setup.md#2-add-the-webhook)); a comment on a pull request arrives as an Issue Comment.
- **Every run needs `.factory/project.json`** in the default branch. Without it the ticket gets a note saying so.
- **Limits.** Revisions per pull request, build rounds, tokens per run and per day: [Limits](project-setup.md#limits) and [Token limits](operations.md#token-limits).
- Why a run stopped, what a status means, and how to resume: [When the factory itself fails](workflows.md#when-the-factory-itself-fails) and [Result statuses](workflows.md#result-statuses).
