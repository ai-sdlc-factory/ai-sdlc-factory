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
| A gate fails on something that works on your machine | The sandbox is a fresh, read-only container each time with no model access. Gates must install their own dependencies and keep generated files in `.gitignore` ([guide](project-setup.md#what-a-good-gate-is)). |
| A run says the image does not exist | Build it: `docker compose --profile images build sandbox-image-<name>`. |
| A run seems stuck | It is probably **waiting for you**: the last comment asks a question (up to 7 days). Otherwise check `docker compose logs controller sandbox-launcher` and the workflow in the Temporal UI. |
| Nothing happens when two tickets run together | Only 2 sandboxes run at once; the others queue (`SANDBOX_MAX_CONCURRENT`). |
| `/fix` or a review did nothing on a pull request | The pull request must be the factory's own (branch `factory/issue-<n>`, authored by the bot, same repository, open) and have fewer revisions than the limit (3 by default, see [limits](project-setup.md#limits)). A review that requests changes needs the webhook's **Pull Request Review Rejected** event; `/fix` needs only Issue Comment. `docker compose logs api` shows each delivery's event name. |
| A reopened ticket did not restart | A `reopened` event starts a new run; check the webhook delivered it. Re-labelling or editing a ticket that already had a run does nothing. |
| The controller was restarted while runs were active | Runs are durable in Temporal and continue; a sandbox that was mid-run is removed at the launcher's next start and the activity is retried or fails with a comment. |
| A network error in the launcher at start | One of the three internal sandbox networks is missing or clashes with yours (`SANDBOX_NET_BASE`). The launcher refuses to run without them. |
| Disk fills up | Old checkouts and Docker layers; see [Cleaning up](#cleaning-up). |

## Cleaning up

- **Checkouts** are deleted when a ticket's workflow ends, even on failure or cancellation. A run you terminated by hand in Temporal can leave its folders behind. They are in
  `FACTORY_WORKSPACE_ROOT`: `<name>/`, `.base/<name>/` and `.records/<name>/`, where `<name>` is the workflow id. It is safe to delete a ticket's three folders when no run for it is active.
  There is no automatic cleanup of these yet.
- **Leftover sandbox containers** (labelled `dev-foundry.sandbox=true`) are removed whenever the launcher starts.
- **Branches** the factory created (`factory/issue-<n>`, `-wip`) stay in Forgejo until you delete them. Delete a `-wip` branch once you have used or dismissed it.
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
| workflow scenarios | part of the controller tests: they run the real workflows on a Temporal dev server with scripted agents and fake activities |
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
Reference points from real runs: a small feature in the full process used about 52,000 tokens (about 28,000 without cache reads), and the same kind of ticket in quick mode about 28,000. There are **no limits or budgets yet**; the design for them (a per-ticket budget first) is in
[Design](design.md#not-built-yet).

### Spending less

The main knobs, from the biggest effect down: `limits.build_rounds` (each failed round is another developer run, the checks and the reviews), `mode: "quick"` (skips the coverage check, design review and QA), and `limits.clarification_rounds`. Set them per project in
[`.factory/project.json`](project-setup.md#more-examples), or for all projects with the `FACTORY_DEFAULT_*` settings in [Configuration](configuration.md). The run record's usage table shows which roles used the tokens, so you can see what a change would save.

## Keeping the documentation correct

`python3 tools/check-docs.py` (CI runs it) checks that every relative link and anchor in the Markdown files resolves, that every image exists, that the diagram sources and their rendered SVGs are both present, that every setting in
`infra/.env.example` and every environment variable the services read is documented in [Configuration](configuration.md), and that every agent role file is listed in the docs. The diagrams are Graphviz sources in
[`docs/diagrams/`](diagrams/); after editing one, run `sh docs/diagrams/render.sh` (needs only Docker) and commit the SVG.

## Known limitations

| Limitation | Notes |
| --- | --- |
| No automatic cleanup of old checkouts and `-wip` branches | see above |
| No token or spending limits | recorded, not enforced |
| Pull request revisions | only the factory's own pull requests are revised; the factory cannot resolve conversations (no API), and anyone who can comment on the pull request can start a revision |
| Incident tickets are written by people | a collector that files them from monitoring waits until there is a production system |
| Unknown keys in `.factory/project.json` are ignored silently | check spelling against the [reference](project-setup.md#reference) |
| Forgejo only | the router reads Forgejo's webhook format |
| One controller instance | the workspace folder is local to it; a second controller would need a shared or per-worker workspace |
| Temporal history is kept 7 days by default | use the run record to keep a run |
