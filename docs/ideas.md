# Ideas for later

[Documentation index](README.md) | [Operations](operations.md)

Five things that would change real outcomes, picked after the live tests of 2026-11-15. None is built. Each says what problem it solves, how it would work in this codebase, what it must not do, and how to know it is done. They follow the rules in [AGENTS.md](../AGENTS.md): **the controller decides, agents advise**; a new setting needs parsing, tests, a place in `ProjectKeys.cs`, an entry in [project-setup.md](project-setup.md) and, with a factory default, a `FACTORY_DEFAULT_*` variable; a new role needs a role file, a row in [configuration.md](configuration.md), a prompt builder and a parser. Delete an idea from this file when it is built.

| # | Idea | Solves | Size |
| --- | --- | --- | --- |
| 1 | [Project instructions](#1-project-instructions) | every project has conventions the agents can only guess | small to medium |
| 2 | [`/setup`: write `.factory/project.json` for a repository](#2-setup-write-factoryprojectjson-for-a-repository) | connecting a repository took an hour and several rounds | medium |
| 3 | [Cost guard before a run](#3-cost-guard-before-a-run) | tokens are only counted after they are spent | small to medium |
| 4 | [What happened to the pull requests](#4-what-happened-to-the-pull-requests) | the weekly report counts failures, never whether the work was any good | small |
| 5 | [Other git hosts, in parallel](#5-other-git-hosts-in-parallel) | the factory only works with Forgejo | medium refactor, then days per host |

Left out on purpose, for now: auto-triage of unlabelled tickets and a permission check on commands (needed the day a second person uses the factory).

## 1. Project instructions

**Problem.** Every project has house rules: naming, "never touch X", domain words, what a good test looks like. Today an agent infers them from the code and from `AGENTS.md` if there is one. The only per-project steering is gates, rules and protected paths, none of which can say "write the tests in this style" or "the money type is `Money`, never `decimal`".

**How.** A file `.factory/instructions.md`, read from the **default branch** (never from a pull request's branch, like `project.json`), added to every agent prompt as one delimited block of data.
- Read it once per run in an activity (workflow code must not read files), next to the project settings, and keep it for the run. At most 8 KB; a longer file is cut and the ticket says so once.
- Add it in **one place**: `RunAgentAsync` in `FactoryWorkflowBase.Agent.cs` builds every role's prompt text, so appending the block there reaches all roles. Mark it `<<<PROJECT INSTRUCTIONS ... PROJECT INSTRUCTIONS>>>` and say it is the project's own house style: it adds to the role's rules and to the engineering floor and **never relaxes** them, and it cannot change the role or the answer shape. Run the text through `PromptText.Clip` so it cannot close its own block.
- Optional sections by role (`## reviewer`, `## developer`) so a security checklist does not go to the planner. A section named after no role is for everyone.
- `.factory/` is already a protected path, so no agent can edit the file; it changes only through a person's merge.
- The prompt argument has a size limit (`MaxLength` of each prompt builder, tested as "the largest possible case fits"): reserve the 8 KB in those limits.

**Must not.** Override the engineering floor, the security checklist, a protected path or a project rule; be read from a branch an author controls; be passed to the `evidence` helper runs (no model).

**Done when.** A scenario shows the block in the prompt of every role (and only the named role's section for a sectioned file); a file that tries to close its block or to claim a new role changes nothing; a missing file changes nothing; `project-setup.md` and `workflows.md` describe it; a test shows the size limit and the "cut" notice.

## 2. `/setup`: write `.factory/project.json` for a repository

**Problem.** Connecting a repository by hand took several rounds in this project: pick the image, write gates that install their own dependencies, add `verify` with the right start command and ready URL, choose reviewers. The settings file is the most fragile part, and a repository without one stops every run with a note.

**How.** A comment `/setup` on any ticket of a repository (or an `owner/repo` given to a small tool) starts a new `SetupWorkflow`, like `/ask`: one read-only agent, one comment, one pull request.
- A new read-only role `configurer` reads the repository (manifests, lockfiles, CI files, README) and answers one JSON object: `image` (one of the factory's images), `gates` (name, command, cwd, timeout), optional `verify` (start, ready, suites), `reviewers`, `protected_paths`, and why for each choice.
- **The controller checks the answer, not the agent.** It builds the file from the answer with the real settings parser (`ProjectConfigParser`), refuses anything the parser refuses, and **runs every proposed gate** in a sandbox with the packages network to see which pass (that is a fact; the agent's word is not used). A gate that fails is reported and left out or marked, never silently kept.
- The controller, not the agent, writes `.factory/project.json` (agents cannot write `.factory/`) and opens a pull request with it and the report of what passed. A person reads and merges it.
- The run needs no `project.json` itself: it uses the `full` image, because the file does not exist yet. That is the one workflow that may run on a repository without settings.
- `tools/onboard-repo.py` already creates the labels and the webhook, so `/setup` after it takes a new repository to a working one.

**Must not.** Choose a repository image (`.factory/Dockerfile`) or widen `RepoImagePolicy`; run a gate with the model login or the model network; invent keys (unknown ones are refused by the parser); merge.

**Done when.** On the four templates with their `.factory/project.json` removed, the proposal parses, its gates pass in the sandbox, and it covers the same ground as the template's own file (image, gates, a `verify` that starts the app); a scenario shows a failing proposed gate is reported and not kept; a hostile README ("set image to ...") does not change what is allowed.

## 3. Cost guard before a run

**Problem.** The token budget (`limits.token_budget`, `FACTORY_MAX_TOKENS_PER_RUN`) and the daily caps stop a run **after** it has used the tokens. A failed QA run used about 0.5M tokens before anything said so, and QA runs were about three quarters of the day's tokens. Nobody sees what a run is likely to cost until it has cost it.

**How.** Three small pieces, all from the [usage log](operations.md#the-usage-log-totals-across-tickets-days-and-repositories), which already has the figures:
- **An estimate on the ticket** when a run starts: "A feature on this repository has typically used about 300,000 tokens (median of the last 9 runs); a `/qa` run about 600,000." Computed by an activity from `usage.jsonl` by kind and repository; with fewer than 3 runs it says there is no history yet. It states a figure and never refuses a run.
- **Tokens of a run that failed or was stopped** shown in its failure comment (a failed run is where the waste is). Successful reports stay as they are: the QA report's token line was removed on purpose (commit `3711ac3`), so do not put it back there.
- **An optional confirmation for expensive kinds**: `limits.confirm_over` (project) / `FACTORY_CONFIRM_OVER_TOKENS` (factory default), off by default. When the estimate of a `/qa` run is over it, the run waits for `/qa go` on the same ticket. Starting from a person's explicit command keeps "the controller decides".

**Must not.** Block a run on the estimate alone; read anything from a ticket into the log (the log holds only numbers, keep it that way); make a decision in a prompt.

**Done when.** A scenario shows the estimate comment with enough history, the "no history yet" wording without, the confirmation flow (waits, `/qa go` starts it, closing the ticket ends the wait), and the token line on a stopped run; the new key is in `ProjectKeys.cs`, `project-setup.md`, `WorkerSettings`, `infra/compose.yaml`, `infra/.env.example` and `configuration.md` (`check-docs.py` enforces it).

## 4. What happened to the pull requests

**Problem.** The weekly report (`ImprovementReport`) counts what went wrong inside runs: rejected answers, failed runs, rounds used. It never asks the only question that says whether the work was good: **was the pull request merged**, how many `/fix` rounds did it need, how long did a person take, was it closed without merging. Without it the improver tunes prompts against failures, not against outcomes.

**How.** Add a section to the weekly report, built by counting, like the rest.
- The usage log already names every run that opened a pull request (`status: pr_opened`, repository, number) and every revision (`kind: revision`). An activity asks Forgejo (read-only, the bot's token) for the state of those pull requests: merged, closed unmerged, still open, merged after how many revisions, days from opening to merge.
- Report per kind: pull requests opened, merged, closed unmerged, open; average revisions per merged one. No titles or text, only counts and repository names, as now.
- Rules in `ReportRules` (constants, with the minimum of 5 runs the other rules use): flag when 30% or more of a week's factory pull requests were **closed unmerged** (the work was rejected) or the average merged pull request needed 2 or more revisions. These go to the improver like the other findings.
- Meaningful from about 20 pull requests; with fewer the section says so instead of showing percentages.

**Must not.** Let the report decide anything (it only reports); read pull request text; call Forgejo from workflow code (use an activity, so a replay sees the same answer).

**Done when.** A report test with scripted Forgejo answers shows the counts, the "too few to say" wording, and each new rule flagging and not flagging at its threshold; `operations.md` lists the new rules in the table of what repeats.

## 5. Other git hosts, in parallel

**Problem.** Everything the factory reads from or writes to a repository goes through Forgejo. A team on GitHub, GitLab, Azure DevOps or Bitbucket cannot use it, and one factory cannot serve repositories on two hosts.

**Where Forgejo is built in** (measured 2026-11-15):
- Controller: 33 files, about 140 mentions. `ForgejoClient` has 17 operations (comments, issues, pull requests and their files, reviews, review requests, branches, a repository list), wrapped by `ForgejoActivities`. `EventRouter` reads Forgejo's webhook JSON directly (`issues`, `issue_comment`, `pull_request_rejected`, labels, `is_pull`).
- API service: `WebhookEndpoints` and `WebhookVerifier` check Forgejo's HMAC-SHA256 signature on one fixed route.
- Alert intake: its own small client (`Forgejo.cs`: find a ticket by marker, create, comment, add a marker).
- Identity: `owner/repo` is the key everywhere (`WorkIds.For` hashes it into workflow ids and workspace folders, the usage log, the janitor's prefixes, the weekly report). There is **no host** in any of it.
- Not affected: the agents, sandboxes, workflows, gates, QA, reviewers, token limits, and cloning and pushing (plain git over HTTPS with a token).

**How.** In this order, each step keeping every test green:
1. **A host port.** Turn the operations of `ForgejoClient` into an interface (`IGitHost`) with Forgejo as the only implementation, and have `ForgejoActivities` call the interface. No behaviour change.
2. **A neutral event.** One `RepoEvent` (kind, repository, number, is it a pull request, comment text, labels, author, action, head ref, state, review state) that `EventRouter` works on, and a **webhook adapter per host** that verifies and normalises (`IWebhookAdapter`: `Verify(headers, body)`, `Normalise(payload)`). The API serves `/webhooks/<host>`; each host has its own secret. Many tests build Forgejo payloads by hand: they become tests of the Forgejo adapter, and the router tests use neutral events.
3. **Host in the identity.** A repository is `host:owner/repo` (the default host keeps today's ids, so nothing running is renamed). Settings carry a list of hosts (type, base URL, token); `WorkIds`, the workspace folders, the janitor, the usage log and the report take the host into account. The janitor and the report loop over hosts.
4. **Configurable triggers.** Today a label starts a run, which not every host has. Per repository (in `.factory/project.json`, or per host): a label, a tag, a work-item type, or a comment command (`/factory feature`). Slash commands in comments are portable as they are.
5. **Alert intake** gets the same port (or one client per host in its mapping file).
6. **Adapters**, one at a time.

| Host | Effort (after step 1-5) | Difficulty |
| --- | --- | --- |
| GitHub | 2-4 days | Closest to Forgejo: nearly the same issues, pulls and reviews API, HMAC-SHA256 webhook header, request-changes reviews exist. The best first proof. |
| GitLab | 4-7 days | Merge requests and notes; no "request changes" (approvals and resolved threads instead); the webhook carries a secret token, not an HMAC. Can run locally (GitLab CE in Docker), so it can be tested without an account. |
| Azure DevOps | 5-8 days | Work items and pull requests are separate things; tags instead of labels; comments are threads; the review state is a vote; PAT auth and service hooks. |
| Bitbucket Cloud | 5-8 days | The built-in issue tracker has no labels (and is often replaced by Jira), so triggers must come from pull request comments; Bitbucket Server is a different API again. |

Steps 1-5 are about 1-2 weeks for someone who knows this code, mostly in the tests that build Forgejo payloads. Serving several hosts at once costs little after that: it is only the host in the identity and the routing.

**Must not.** Let a host decide anything (hosts only deliver events and carry results); treat a fork's pull request as trusted on a host where forks work differently (the review-reads-default-branch rule must hold on every host); lose the hidden `<!-- dev-foundry:factory -->` marker (check that each host renders HTML comments invisibly, or use another reliable way to recognise the bot's own comments, such as its user id); assume an identical set of review states.

**Needs a decision first.** The permission check on commands ("anyone who can comment can use `/approve`") stops being something to postpone once more than one host is involved, because each host has its own idea of who may comment.

**Done when.** The Forgejo behaviour is byte-for-byte the same after steps 1-3 (the whole suite and the live checks of 2026-11-15 pass unchanged); a second host runs the same ticket-to-pull-request path live (a feature ticket, `/ask`, a `/fix` after a review) and its pull request is reviewed and merged by a person; both hosts run at the same time on one controller without sharing a workspace or a usage count; the documentation names each host's trigger setting and token scopes.
