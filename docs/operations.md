# Operations

[Documentation index](README.md) | [Getting started](getting-started.md) | [Configuration](configuration.md)

Running the factory day to day: seeing what a run did, fixing common problems, cleaning up, and the practical security checks. Commands run from `infra/` unless noted.

## Seeing what a run did

| You want to know | Look here |
| --- | --- |
| what the factory is doing right now | the **ticket**: every stage posts a comment, and a stopped run says why |
| every prompt, answer and check output of a run | **Temporal UI** (http://localhost:8080), namespace `default`: open the workflow whose id starts with `feature-`, `bug-` or `incident-` and the repository name; the stopped-run comment links to it. Kept for `TEMPORAL_RETENTION` (7 days) |
| the same, kept for good, in the repository | the [run record](workflows.md#the-run-record) under `.factory/runs/` on the pull request branch (set `"record": true` in `.factory/project.json`) |
| how many tokens a ticket used, per role | `usage.json` and the usage table in that run record |
| work from a run that could not finish | the `factory/issue-<n>-wip` branch (named in the comment) |
| what a sandbox tried to reach on the internet | `docker compose logs egress-proxy`; refused hosts: `docker compose logs egress-proxy \| grep '"decision":"deny"'` |
| which sandboxes are running and for which ticket | `docker ps --filter label=dev-foundry.sandbox=true --format '{{.Names}} {{.Label "dev-foundry.workspace"}} {{.Label "dev-foundry.role"}}'` |
| why a service misbehaves | `docker compose logs -f <service>` (`api`, `controller`, `sandbox-launcher`, `egress-proxy`, `temporal`, `forgejo`) |

Temporal workflow ids look like `feature-<owner>-<repo>-<number>-<hash>` (also `bug-` and `incident-`). Each webhook delivery is its own short `forgejo-<delivery-id>` workflow that
shows what the router decided.

## Troubleshooting

| Symptom | Cause and what to do |
| --- | --- |
| A ticket gets no comment | The webhook is missing, lacks the Issues / Issue Label / Issue Comment events, or has the wrong secret. In Forgejo use **Test Delivery** (`202` is good), then `docker compose logs api`. The ticket must also carry a factory label. |
| "FORGEJO_BOT_TOKEN is not configured" | Set the token in `infra/.env` and `docker compose up -d --force-recreate controller`. |
| "OpenCode is not signed in" | Run the [login](opencode-login.md); nothing needs restarting. The volume is lost by `docker compose down -v` or a Docker reset. |
| A role fails with "exited with code ..." and no explanation | Usually a model your account cannot use. Check the `model:` line of that role and `opencode models openai` ([agents guide](../agents/README.md#choosing-the-model-per-role)). |
| "`.factory/project.json` cannot be used" | The comment names the problem. Fix the file on the default branch and close and reopen the ticket. |
| Front-end tests fail with "Test timed out in 5000ms" although the whole run took a few seconds, in a different test each time | Not your tests: the **machine's clock is stepping** (seen on WSL2: `Date.now()` jumps +9 s and back every few seconds), and test runners that time out on the wall clock fire early. Check: `python3 tools/check-clock.py` (about 15 seconds) prints "The clock is steady" on a healthy machine and lists the steps otherwise. Restart WSL (`wsl --shutdown` from Windows) and check the time-sync service (`timedatectl`); until it is fixed, give the tests long timeouts (the [templates](../templates/README.md) do: `testTimeout` and `asyncUtilTimeout`). The same clock can make the scenario tests slow |
| A gate fails on something that works on your machine | The sandbox is a fresh, read-only container each time with no model access. Gates must install their own dependencies and keep generated files in `.gitignore` ([guide](project-setup.md#what-a-good-gate-is)). |
| A run says the image does not exist | Build it: `docker compose --profile images build sandbox-image-<name>`. |
| A run seems stuck | It is probably **waiting for you**: the last comment asks a question (up to 7 days). Otherwise check `docker compose logs controller sandbox-launcher` and the workflow in the Temporal UI. |
| Nothing happens when two tickets run together | Only 2 sandboxes run at once; the others queue (`SANDBOX_MAX_CONCURRENT`). |
| `/fix` or a review did nothing on a pull request | The pull request must be the factory's own (branch `factory/issue-<n>`, authored by the bot, same repository, open) and have fewer revisions than the limit (3 by default, see [limits](project-setup.md#limits)). A review that requests changes needs the webhook's **Pull Request Review Rejected** event; `/fix` needs only Issue Comment. `docker compose logs api` shows each delivery's event name. |
| `/update` did nothing, or says a person has to merge | The pull request must be the factory's own and open (as for `/fix`), and the comment must have `/update` alone on its line. A conflict in a **protected file** (`.factory/`, CI files, `protected_paths`) or in more than 40 files is left to a person on purpose; merge the base branch by hand, push, and the next `/update` says `up_to_date`. "The branch changed while the update was being made" means someone pushed meanwhile: comment `/update` again. |
| `/review` posted nothing, or says it could not read the changes | It needs an open pull request and Forgejo's `pulls/<n>/files` and `.diff` (the bot's token must be able to read the repository). A closed or merged pull request is left alone. `/review` works on **anyone's** pull request, and reads it from the default branch's settings, so a fork cannot change what runs. |
| A `docs` ticket opened no pull request | `no_changes` means the writer found the documents already right. Otherwise read the ticket: a change outside `AGENTS.md`, `README.md`, `CONTRIBUTING.md` and `docs/**/*.md` (or a deletion) goes back to the writer, and after `limits.build_rounds` rounds the run stops with `build_failed`. |
| A failure comment says "a problem with the factory itself" | The sandbox stopped reporting progress, or the launcher or Docker was unavailable (see `infrastructure_failed`). Nothing is wrong with the ticket: check `docker compose ps` and the launcher's logs, then comment `/retry` (it carries on from the last stage that finished; `/retry restart` starts over) |
| `/qa` says the project has no `verify` settings, or the app "did not start" | Add or fix `verify` in `.factory/project.json` on the default branch **and merge it into the pull request's branch** (a QA run reads the pull request's branch). Check `start`, `ready` (the port the app really listens on) and `startup_timeout_seconds`; the report shows the end of the application's log |
| A reopened ticket did not restart | A `reopened` event starts a new run; check the webhook delivered it. If the ticket was closed only a moment before, its earlier run may still be stopping (a run notices a closed ticket between steps): the factory waits up to about 30 minutes, starts the new run when the old one has ended, and otherwise says so on the ticket (then comment `/retry restart`). Re-labelling or editing a ticket that already had a run does nothing. |
| Several agents' answers are rejected again and again (the planner's "questions" is missing, `/ask` has a wrong verdict, the acceptance check never completes), or the ticket says an agent's role file is missing | The agents folder the sandboxes get (`FACTORY_GLOBAL_CONFIG`) is an out-of-date **copy** of the repository's `agents/`: roles added since the copy was made are not there, and OpenCode then answers as its default agent, without the role's instructions. Point `FACTORY_GLOBAL_CONFIG` at the repository's own `agents/` folder, or copy it again after every update (every file, including new roles and `standards/`), then comment `/retry`. The controller also logs an error at start naming the roles that have no file (`docker compose logs controller`), and the run record shows `agent "<role>" not found` on the agent's standard error. |
| `/retry` says the ticket already has an open pull request | A new run would open a second pull request. Comment `/fix <what to change>` on that pull request, or `/retry restart` if you do want a new one. |
| The launcher logs "Temporal at ... is not reachable yet; waiting for it" | Normal while Temporal is starting or was just recreated (`docker compose up -d --build`): the launcher waits up to 5 minutes for it, then starts its worker anyway and fails loudly if it still cannot connect. |
| The controller was restarted while runs were active | Runs are durable in Temporal and continue; a sandbox that was mid-run is removed at the launcher's next start and the activity is retried or fails with a comment. |
| A network error in the launcher at start | One of the three internal sandbox networks is missing or clashes with yours (`SANDBOX_NET_BASE`). The launcher refuses to run without them. |
| Disk fills up | Old checkouts and Docker layers; see [Cleaning up](#cleaning-up). |

## Several git hosts

The factory serves one Forgejo by default. With [`FACTORY_EXTRA_HOSTS`](configuration.md) it can serve more hosts at once (today other Forgejo instances; each kind of host needs an adapter, see [architecture](architecture.md#how-a-ticket-flows)) from one controller.

- **Names.** A host has a name (`codeberg`). The default host is called `forgejo`. A repository on any other host is written **`<host>:owner/repo`** everywhere the factory names a repository: in comments that name it, in the usage log's `repository` field, in the weekly report, and in the ids of workflows and the names of checkout folders (which carry a hash of it). The default host's repositories keep the plain `owner/repo`, so **nothing that exists is renamed** when a second host is added.
- **Nothing is shared.** The same owner, repository and ticket number on two hosts are two different runs: different workflow ids, different checkouts, different usage counts. A token is used only for its own host (the clone remembers which in `.git/factory-host`), and every host's token is kept out of logs, run records and pushed files.
- **Setting one up.** Add it to `FACTORY_EXTRA_HOSTS`, restart the api and the controller, give the bot account on that host write access to the repositories, and add a webhook there that points at `http://<api>/webhooks/<name>` with the host's `webhook_secret` (the same events as for the default host: [Setting up a project](project-setup.md#2-add-the-webhook)). The labels have to exist on each host's repositories; `tools/onboard-repo.py` only knows the default host, so create them by hand or point the tool at the other host.
- **What loops over hosts.** The janitor lists every host's writable repositories (a host that cannot be listed fails that pass rather than leaving a partial list), and the weekly report asks each host about its own pull requests, with each host's own bot login. The repository for the report's ticket (`FACTORY_IMPROVEMENT_REPO`) is on the default host.
- **Triggers.** How a host's tickets say which workflow to run is per host: `FACTORY_TRIGGERS` for the default host, a `triggers` object in each extra host's entry. They add label (or tag) names per kind and work-item types per kind, and can turn on the `/factory <kind>` comment, which every host has because every host has comments. The router reads them through an activity, so a replayed delivery is routed by the rules it first met. They are per host, not per repository, because the router decides before any repository is read.
- **Not yet.** Alert intake still opens its tickets on the default host only.

## Cleaning up

A **janitor** in the controller cleans up once a day (the first pass is five minutes after the controller starts). It is on by default and cautious: it only removes things that are provably finished, and anything it cannot establish it leaves alone. Everything it does is written to the controller log: `docker compose logs controller | grep Janitor`.

| What | Rule | Setting |
| --- | --- | --- |
| **Leftover checkouts** (`<name>/`, `.base/<name>/`, `.records/<name>/` in `FACTORY_WORKSPACE_ROOT`, and the ticket's saved stages `.resume/<name>.json` that `/retry` reuses) | removed when the run with that id is **not running** (asked of Temporal) **and** nothing has touched the folders for 2 days. Only folders named after a run (`feature-`, `bug-`, `incident-`, `revision-`, `qa-`, `update-`, `review-`, `docs-`, `ask-`, `improvement-`) are ever touched, never anything else in that folder. If Temporal cannot be asked, nothing is removed | `FACTORY_JANITOR_CHECKOUT_DAYS` |
| **Old `-wip` branches** (`factory/issue-<n>-wip`) in repositories the bot can push to | **listed** in the log once their last commit is 30 days old. **Deleted** only if you set `FACTORY_JANITOR_DELETE_WIP=true` **and** their ticket is closed **and** no open pull request uses them. Pull request branches (`factory/issue-<n>`) are never touched | `FACTORY_JANITOR_WIP_DAYS`, `FACTORY_JANITOR_DELETE_WIP` |
| **Repository images** (`dev-foundry/repo:*`, only while `FACTORY_ALLOW_REPO_IMAGES` is on) | removed once a day when older than 60 days and no container uses them. They are rebuilt on the next use, so the only cost is a build. The launcher does this | `FACTORY_JANITOR_IMAGE_DAYS` (0 = never) |

Switch it off with `FACTORY_JANITOR=false`. The log line to look for after a pass: `Janitor: removed 3 old checkout(s); 1 old work-in-progress branch(es) found, 0 deleted; 0 problem(s)`.

Still manual:
- **Leftover sandbox containers** (labelled `dev-foundry.sandbox=true`) are removed whenever the launcher starts.
- **Pull request branches** (`factory/issue-<n>`) stay until you delete them after merging.
- **Docker**: `docker image prune` and `docker builder prune` reclaim build layers; do not prune volumes (`docker volume prune`) unless you are sure: `opencode-data` holds the login, `forgejo-data` your repositories.

## Backups and data

Everything that matters is in five named volumes: `forgejo-data` (repositories, tickets), `postgres-data` (Forgejo and Temporal databases), `pgadmin-data`, `mailpit-data`, and `opencode-data`
(the login, replaceable by signing in again). A simple backup stops the stack and archives the volumes:

```bash
docker compose stop
docker run --rm -v dev-foundry_forgejo-data:/data -v "$PWD":/backup alpine tar czf /backup/forgejo-data.tgz -C /data .
docker run --rm -v dev-foundry_postgres-data:/data -v "$PWD":/backup alpine tar czf /backup/postgres-data.tgz -C /data .
docker compose up -d
```

`docker compose down --volumes` deletes all of it. Passwords in `.env` do not update existing database accounts when changed.

## Updating

- **Factory code:** `git pull`, then `docker compose up -d --build` (or name a service: `docker compose up -d --build controller`).
- **Sandbox images:** `docker compose --profile images build sandbox-image-<name>` after a change to `sandboxes/`. The OpenCode version in the images is pinned and checked against a digest.
- **Agents:** edit `agents/agents/<role>.md` and commit; the next agent run uses it. Running sandboxes keep the copy they started with.
- **Egress rules:** edit `egress/profiles.json` (or your own file) and `docker compose restart egress-proxy`.
- Image versions of Forgejo, Temporal, PostgreSQL and the others are pinned in `infra/compose.yaml`; change them there.

## Tests

| What | How |
| --- | --- |
| a service's unit tests | from the repository root: `docker build --target test -f services/<service>/Dockerfile -t t .` then `docker run --rm t` (`api`, `controller`, `sandbox-launcher`, `egress-proxy`) |
| workflow scenarios | part of the controller tests: they run the real workflows on a Temporal dev server with scripted agents and fake activities. `python3 tools/run-scenarios.py` runs them in several containers at once (about 2 minutes instead of 7) and checks that no scenario class is left out |
| the deployed pipeline | `docker compose --profile test run --build --rm --no-deps integration-tests` ([guide](../tests/integration/README.md)) |
| documentation links and settings | `python3 tools/check-docs.py` ([below](#keeping-the-documentation-correct)) |
| CI | `bitbucket-pipelines.yml` runs the service tests, a vulnerable-package check, the documentation check and the integration tests |

## Security notes

- **Tokens.** The bot token needs only `write:issue` and `write:repository`. It lives in `infra/.env` and in the controller; it never enters a sandbox, a repository or Temporal history. To rotate it, create a new token,
  update `.env`, recreate the controller, and revoke the old one. To rotate the webhook secret, change `FORGEJO_WEBHOOK_SECRET` and update **every** repository's webhook.
- **Local only.** All ports bind to `127.0.0.1`. Temporal, Mailpit and pgAdmin have no authentication of their own; do not publish them.
- **Egress mode.** Keep `SANDBOX_EGRESS=enforce`. `audit` is for finding out what a tool needs and logs everything over HTTPS; `open` removes the restriction entirely and the launcher prints a warning at start.
- **Who can steer a run.** Anyone who can comment on a ticket can answer an agent's questions and, on incidents, use `/hotfix`. Forgejo's repository permissions are the access control.
- **Review every pull request** like any other. The checks catch protected files, symbolic links, oversized files and common credential shapes, but they are a safety net, not a guarantee.
- **What leaves your machine:** prompts, which contain ticket text and code excerpts, go to the model provider. Do not put secrets in tickets. Logs in incident tickets can contain personal data.
- **Repository text is untrusted.** A hostile ticket or repository file can try to instruct an agent; roles are told to treat it as data, permissions are fixed per role, and the sandbox has no route to anything but the allowlist.
- **Repository images are off by default.** With `FACTORY_ALLOW_REPO_IMAGES=true`, whoever can merge to a repository's default branch can run build instructions on this machine's Docker, with internet access (the egress proxy covers sandboxes while they run, not image builds). Enable it only for repositories you control. The factory limits what a Dockerfile may contain, how long and how large a build may be, and never builds one an agent wrote, but it is still the same trust as running that repository's CI. Built images pile up: `docker image ls dev-foundry/repo`.

## Cost and usage

With a ChatGPT login there is no per-token price: usage counts against your plan, and OpenCode reports `cost` as 0 (the field is kept, so an API key would show real figures). The run record lists tokens per role and step.
Reference points from real runs: a small feature in the full process used about 52,000 tokens (about 28,000 without cache reads), and the same kind of ticket in quick mode about 28,000. Tokens can be capped, per run and per day: see [Token limits](#token-limits).

### The usage log: totals across tickets, days and repositories

Every finished run that used an agent adds **one line** to `usage.jsonl` in the folder `.usage/` under `FACTORY_WORKSPACE_ROOT` (a host path, so you can read it without going into a container; no sandbox can reach it). The file only grows (about 300 bytes a run), it is never rewritten, and it holds no text from tickets or code: only which run it was and what it used.

```json
{"time":"2026-11-14T10:00:00Z","workflow_id":"feature-acme-shop-1-ab12cd34","run_id":"…","kind":"feature","repository":"acme/shop","number":1,"status":"pr_opened","input":1000,"output":200,"reasoning":10,"cache_read":500,"cache_write":0,"total":1710,"cost":0,"steps":5,"partial":false,"rounds":2,"gate_failures":1,"roles":{"developer":{"attempts":2,"rejected":0,"failed":0,"tokens":900},"analyst":{"attempts":2,"rejected":1,"failed":0,"tokens":400}}}
```

`kind` is `feature`, `bug`, `incident`, `plan`, `revision`, `qa`, `update`, `review`, `docs`, `ask`, `setup` or `improvement`; `number` is the ticket or, for a revision, QA run or update, the number of the ticket or pull request the comment was on; `status` is how the run ended (a run that stopped is logged too, because its tokens were spent); `partial` is true when an agent's usage could not be read in full, so the total is a lower bound. `rounds` is the most rounds a loop of the run reached (build, revision, update or documentation rounds), `gate_failures` how many times one of the project's checks failed, and `roles` what each agent did in the run: `attempts` made, answers `rejected` (unusable, or against a rule) and runs that `failed` outright, and the `tokens` it used. A run whose agents all failed before using any tokens still gets a line, because its failures are what the [weekly report](#the-weekly-report-what-repeats) looks for. A run that stopped before any agent ran (no settings file, a repository image that would not build) adds no line. Lines written before these figures existed simply lack them. Nothing from a ticket (its title, text or comments) is ever written to it. A line is added when the run ends, so a run that is still going is not in it yet.

Totals, from the repository root:

```sh
python3 tools/usage-report.py                                  # by repository (reads the root named in infra/.env)
python3 tools/usage-report.py --by day --since 2026-11-01     # by day; also month, kind, status, ticket
python3 tools/usage-report.py --by kind --repo acme/shop      # filter by --repo, --kind, --since, --until
```

The report counts each run once even if a line was written twice (by `run_id`), skips a line it cannot read and says so. It is plain JSON lines, so `jq` or a spreadsheet work as well. To keep the file small, move it away when you like (the controller creates a new one); nothing reads it back except you and this tool.

### The weekly report: what repeats

Once a week (a few minutes after the controller starts and then checked every hour, so a restart never makes a second one) the factory evaluates the **last completed week** (Monday to Monday, UTC) by **counting**: no model is involved, the same logs always give the same report. It reads the [usage log](#the-usage-log-totals-across-tickets-days-and-repositories) and the egress proxy's log of refused hosts, and saves `.usage/reports/<year>-W<week>.md` next to the usage log. A rule only flags something when there is enough to mean something (at least 5 runs or attempts), and all of them are constants in [`ReportRules.cs`](../services/controller/src/DevFoundry.Controller/Improvement/ImprovementReport.cs):

| What repeats | Flagged when |
| --- | --- |
| **rejected answers**, per agent | at least 20% of an agent's answers were unusable or against a rule (a prompt or an answer shape that is unclear) |
| **failed agent runs**, per agent | at least 20% of its runs failed outright (no answer, a timeout, an error) |
| **rounds used**, per kind of run | at least 30% of the runs needed 3 rounds or more |
| **runs that stopped the same way** | 3 or more runs of a kind ended the same unhealthy way (anything but a pull request, a posted result or a close) |
| **pull requests turned down**, all kinds | at least 30% of the week's factory pull requests (and at least 5) were **closed without being merged**: the work was rejected, whatever the checks said |
| **pull requests needing revisions**, all kinds | the week's **merged** factory pull requests (at least 5) needed **2 or more revisions** on average: they were not right the first time |
| **token outliers** | a run used 3 times the median of its kind (needs 5 runs of the kind) and at least 50,000 tokens |
| **refused hosts** | the same host was refused 3 or more times (from the proxy's refusal log). **Plain HTTP requests** are not counted here: the proxy only carries HTTPS tunnels, and build tools make hundreds of plain requests per build (.NET's certificate revocation checks to `ocsp.digicert.com` and `crl3.digicert.com`, found in the weather build), which fail softly. The report counts them on one separate line and never flags them |

**What became of the pull requests.** The report also has a section on the pull requests the factory opened that week: per kind, how many were opened, merged, closed without merging or are still open, the revisions each merged one needed, and the days from opening to merge. The usage log says which repositories, which tickets and how many revisions (`revision_pushed` runs per pull request); a **read-only** question to Forgejo says what became of each (state, merged or not, dates: no title or text of any pull request is read). It counts only pull requests the bot opened from a `factory/issue-<n>` branch. Percentages are shown from 20 pull requests in the week; with fewer the section gives the counts and says that too few were opened to say more. If Forgejo cannot be asked the section says so and the rest of the report is unchanged. The figures are as of the moment the report is made (the Monday after the week), so a pull request merged later counts as still open.

Without anything to flag, or with no repository set, that is all: a file. To have the factory use it, set `FACTORY_IMPROVEMENT_REPO=owner/name` (usually the factory's own repository on Forgejo; it needs a `.factory/project.json`, because an agent reads it). When the report flags something, the factory then:

1. opens the report as a **ticket** in that repository, titled "Factory report, 2026-W46". It carries **no factory label**, so no workflow starts on it;
2. runs the read-only `improver` agent on a checkout of that repository (no shell, no editing; the model only), which proposes up to 8 changes to prompts, settings, allowlists or code as **one comment** on the ticket: what to change in which file, which numbers in the report say so, and what could get worse;
3. checks only that every proposal names a file that exists (a proposal naming one that does not is left out, and the comment says how many);
4. stops. **Nothing is applied, and nothing will be**: the proposals are suggestions from counts, which cannot see why a run went the way it did. A person reads them, checks the evidence in the report, and makes the change by hand, or files a ticket for it.

The agent's own run costs tokens like any other (kind `improvement` in the usage log, so the next report sees it) and counts against the [token limits](#token-limits). A week with no run, or with nothing flagged, opens no ticket and costs none. Turn the whole thing off with `FACTORY_IMPROVEMENT=false`.

**Refused hosts.** The egress proxy also writes each refusal (one JSON line, at most 10 MB, then it stops writing and says so once) to a volume the controller reads, `egress-log`, at `/var/log/egress/denied.jsonl`. Without that file the report says it knows nothing about refused hosts, rather than claiming there were none. A refusal can be a host that is not on the profile's list, a name that does not resolve or a connection that failed; the report shows the reason.

**Running it by hand** (a week that failed, or to look at one now). A run that ended with `improvement_failed` is not started again by itself, so a broken week cannot loop every hour. From the `infra` folder:

```sh
docker compose run --rm --no-deps --entrypoint temporal temporal-namespace workflow start --task-queue factory-events --type ImprovementWorkflow \
  --workflow-id improvement-2026-w46-again --input '{"Week":"2026-W46","From":"2026-11-09","To":"2026-11-16","Repository":null}'
```

(`Repository` as `"owner/name"` opens the ticket and runs the agent; `null` only saves the report. The command uses the `temporalio/admin-tools` image of the `temporal-namespace` service, which already knows the Temporal address; the same workflow can be started from the Temporal UI with the same input.) Use a new workflow id each time: the week's own id is taken once a run for it has finished.

### Token limits

Nothing is limited until you say so; every limit is off by default. All of them count **input + output + reasoning + cache writes**, and leave out cached input that was only read again (the cheap part). They are checked **before each agent run**, never in the middle of one, so a single long agent run can still go over; the agent's own timeout (15 or 30 minutes) bounds that.

| Limit | Set by | At the limit |
| --- | --- | --- |
| **per run** | `limits.token_budget` in a project's [`.factory/project.json`](project-setup.md#limits), and the operator's maximum `FACTORY_MAX_TOKENS_PER_RUN` (a project can only lower it) | the run stops before its next agent with `budget_exceeded`, **saves what it wrote** to a `-wip` branch and says so on the ticket. Raise the budget and comment `/retry`; a feature or bug run keeps the stages that finished |
| **per day, all repositories** | `FACTORY_DAILY_TOKEN_LIMIT` | the run **waits for the next UTC day** (it says so once on the ticket) and then carries on. Closing the ticket ends the wait |
| **per day, one repository** | `FACTORY_REPO_DAILY_TOKEN_LIMIT` | the same, for runs on that repository |

A budget belongs to one run: a `/retry`, a revision, an update or a QA run is a new run with its own budget. The daily limits are read from the [usage log](#the-usage-log-totals-across-tickets-days-and-repositories): the runs that **finished** today plus what the asking run has used itself. A run that is still going is in the log only when it ends, so several runs working at once can together go a little over a day's limit; and what a waiting run uses after midnight counts toward the new day. Delete or move the log and the day starts from zero.

**The provider's own limit.** When the model provider (your ChatGPT plan, or an API key's quota) says its limit is reached, OpenCode fails and the factory recognises it: the run stops at once with `usage_limit`, saves its work, and says that nothing is wrong with the ticket and to comment `/retry` after the limit resets. It does not try the same call a second time, which would only be told the same. This goes by the provider's **wording** on OpenCode's standard error and its `error` events (for example "usage limit", "rate limit", "too many requests", an HTTP 429 or an exceeded quota), because no document fixes the text. A limit worded differently is reported as an ordinary failure (tried once more, then `analysis_failed`, `build_failed` or the like); if you meet one, the patterns are in one place, `ProviderLimit.cs`.

### Estimates, and what a stopped run used

Tokens are counted after they are spent, so the factory also tells you beforehand what a run is likely to cost and afterwards what a wasted one did. All of it comes from the [usage log](#the-usage-log-totals-across-tickets-days-and-repositories), which holds only numbers.

- **An estimate on the ticket** when a feature, bug, incident, plan, documentation, revision, QA or update run starts: "A feature on this repository has typically used about 300,000 tokens (the median of the last 9 runs)". It uses the runs of the **same kind on the same repository** that used any tokens; with fewer than 3 it says there is no history yet. It is a figure to read and **never stops or delays a run** (the cheap kinds, such as a question or a review, say nothing).
- **The tokens of a run that failed or was stopped** are said once more at its end ("This run used about 480,000 tokens before it stopped"), for every `*_failed` status, `build_blocked`, `error`, `usage_limit`, `cannot_reproduce`, `qa_blocked`, `update_blocked` and `timed_out`, when any were spent. A successful run's report stays as it is.
- **An optional go-ahead for an expensive QA run.** With `limits.confirm_over` in a project (or `FACTORY_CONFIRM_OVER_TOKENS` for all projects; off by default), a `/qa` run whose estimate is over it says so on the ticket and **waits for `/qa go`** before the tester spends anything. Closing the ticket ends the wait, and so do 7 days without an answer (`not_confirmed`). With fewer than 3 earlier runs there is no estimate, so the run starts at once. The run itself was already a person's command: the go-ahead only makes them confirm a cost they have said they care about.

### Spending less

The main knobs, from the biggest effect down: `limits.build_rounds` (each failed round is another developer run, the checks and the reviews), `mode: "quick"` (skips the coverage check, design review and the acceptance check), and `limits.clarification_rounds`. Set them per project in
[`.factory/project.json`](project-setup.md#more-examples), or for all projects with the `FACTORY_DEFAULT_*` settings in [Configuration](configuration.md). The run record's usage table shows which roles used the tokens, so you can see what a change would save.

## Keeping the documentation correct

`python3 tools/check-docs.py` (CI runs it) checks that every relative link and anchor in the Markdown files resolves, that every image exists, that the diagram sources and their rendered SVGs are both present, that every setting in
`infra/.env.example` and every environment variable the services read is documented in [Configuration](configuration.md), and that every agent role file is listed in the docs. The diagrams are Graphviz sources in
[`docs/diagrams/`](diagrams/); after editing one, run `sh docs/diagrams/render.sh` (needs only Docker) and commit the SVG.

## Known limitations

| Limitation | Notes |
| --- | --- |
| The janitor never deletes pull request branches or sandbox images that are still in use | by design; see [Cleaning up](#cleaning-up) |
| Token limits are off until you set them, and are per run and per day | a budget covers one run (a retry, revision, update or QA run has its own), the daily limits count only runs that have finished, and a single agent run can go over a limit by its own length. See [Token limits](#token-limits) |
| Pull request updates | only the factory's own pull requests are updated, and always by a normal push (never forced); a merge conflict in a protected file, or in more than 40 files, is left to a person; anyone who can comment can start one |
| Reviews, updates and documentation runs spend tokens on anyone's request | `/review` works on anyone's pull request, and anyone who can comment can start `/review`, `/qa`, `/fix`, `/update` and `/retry`; nothing checks the commenter's write access yet |
| Pull request revisions | only the factory's own pull requests are revised; the factory cannot resolve conversations (no API), and anyone who can comment on the pull request can start a revision |
| Incident tickets come from people, or from the alert intake | the intake only watches the [demo environment](../demo/README.md); watching a real system needs its alerts routed in the intake's `mapping.json`. It opens a ticket only when `ALERT_INTAKE_DRY_RUN` is `false`, and the factory's incident workflow then spends tokens on every one |
| Unknown keys in `.factory/project.json` only produce a warning | the ticket names the key and the likely spelling, and the run goes ahead without it; see the [reference](project-setup.md#reference) |
| Forgejo only | the router reads Forgejo's webhook format |
| One controller instance | the workspace folder is local to it; a second controller would need a shared or per-worker workspace |
| Temporal history is kept 7 days by default | use the run record to keep a run |
