# Connecting a git host: Forgejo and GitHub

[Documentation index](README.md) | [Getting started](getting-started.md) | [Setting up a project](project-setup.md) | [Configuration](configuration.md#factory_hosts)

Everything the factory needs from a git host, in one place, for each of the two it serves: **Forgejo** and **GitHub**. A host is set up **once** (an account, a token, an entry in `FACTORY_HOSTS`); each **repository** then needs the bot's access, a webhook and the labels. People steer the factory through the host (labels, comments, reviews), so the last section says who may do that and how to restrict it.

## What the factory needs, at a glance

| # | What | Once per host | Once per repository |
| --- | --- | --- | --- |
| 1 | A **bot account** (a normal user, not an administrator of the site) | yes | |
| 2 | A **token** of that account | yes | |
| 3 | An entry in **`FACTORY_HOSTS`** (`infra/.env`) with the host's address, the token, the bot's login and a webhook secret | yes | |
| 4 | **Access for the bot** to the repository: write (and, only for [permission levels](#who-may-start-and-steer-runs), administrator on Forgejo) | | yes |
| 5 | A **webhook** that sends the host's events to the factory's api | | yes (or one per organization) |
| 6 | The **labels** the factory reads | | yes |
| 7 | `.factory/project.json` on the default branch ([setting up a project](project-setup.md#4-add-factoryprojectjson)) | | yes |

The factory talks to a host in two ways only: the host **sends events** to the api (the webhook), and the controller **calls the host's API and runs git** with the bot's token. It does not use e-mail or any notification feature of the host (see [Notifications](#notifications)).

```text
person -> host (label, comment, review) -> webhook -> api (/webhooks/<name>) -> controller -> host API + git (the bot's token)
```

## 1. The bot account

The bot comments on tickets, pushes branches named `factory/issue-<n>` and `factory/issue-<n>-wip`, and opens pull requests. It never merges, and never pushes to a default branch. Give it an account of its own so the factory never acts as a person; **do not** make it an administrator of the site or the organization.

| | Forgejo | GitHub |
| --- | --- | --- |
| **Create it** | As an administrator: Site Administration, User Accounts, Create. Or on the command line (the bundled Forgejo): `docker compose exec -u 1000 forgejo forgejo admin user create --username factory-bot --email factory-bot@example.test --random-password --must-change-password=false` (see [the bundled Forgejo](../companions/forgejo/README.md#the-factory-bot-account)) | An ordinary GitHub user (a "machine user"), for example `factory-bot`, with its own e-mail address. Using your own account works, but the factory's comments and pull requests are then yours, and you cannot review your own pull requests |
| **Its login** | goes into `bot_login` of the host entry (default `factory-bot`). The factory only revises pull requests that account opened, and compares the author with it | the same |

## 2. The token

The token is the bot's credential for both the API and git (clone, push). It stays in `infra/.env`, only the controller uses it to act, it never enters a sandbox, and it is kept out of logs, run records and pushed files.

### Forgejo

Sign in as the bot: **Settings, Applications, Generate New Token**, with the scopes:

| Scope | Why |
| --- | --- |
| `write:issue` | comments, tickets, labels |
| `write:repository` | branches, pull requests, review requests; git over HTTPS; reading what a person may do on a repository |

On the command line (the bundled Forgejo):

```bash
docker compose exec -u 1000 forgejo forgejo admin user generate-access-token \
  --username factory-bot --token-name controller --scopes "write:issue,write:repository" --raw
```

The token is shown once. Put it into `"token"` of the host entry.

### GitHub

A **fine-grained personal access token** of the bot account: **Settings, Developer settings, Personal access tokens, Fine-grained tokens, Generate new token**.

| Setting | Value |
| --- | --- |
| Resource owner | the owner of the repositories (a person or an organization; an organization may have to allow fine-grained tokens and approve this one) |
| Repository access | **Only select repositories**: just the ones the factory should work on |
| Permissions | **Contents**: read and write (clone, push the factory's branches). **Issues**: read and write (tickets, comments, labels). **Pull requests**: read and write (open them, reviews, review requests). **Metadata**: read (GitHub adds it) |
| Expiry | pick one and note the date: when it runs out every call fails until you replace the token |

A classic token with the `repo` scope also works, but it reaches every repository the account can, which is more than the factory needs. If the organization enforces single sign-on, authorize the token for it. GitHub allows 5,000 API requests an hour for a token, far more than the factory uses. A GitHub App is not supported.

## 3. The entry in `FACTORY_HOSTS`

One JSON line in `infra/.env` lists every host ([all the fields](configuration.md#factory_hosts)):

```bash
FACTORY_HOSTS=[{"name":"forgejo","url":"http://forgejo:3000","token":"<forgejo token>","bot_login":"factory-bot","webhook_secret":"<32+ characters>"},{"name":"github","type":"github","url":"https://github.com","token":"<github token>","bot_login":"factory-bot","webhook_secret":"<32+ characters>"}]
```

- `name` ends the webhook route (`/webhooks/<name>`) and prefixes the host's repositories (`github:owner/repo`); a host called `forgejo` keeps plain `owner/repo`.
- `url` is where the **controller** reaches the host. The bundled Forgejo is `http://forgejo:3000` over a Docker network; another Forgejo is its address; GitHub is `https://github.com` (or the address of a GitHub Enterprise Server).
- `webhook_secret` is made up by you (`openssl rand -hex 32`); the same value goes into each repository's webhook on that host.

After a change, restart the api and the controller: `cd infra && docker compose up -d --force-recreate api controller`. The controller logs each host at start (`Git host github (github): anyone who can comment may use commands`).

## 4. Access of the bot to a repository

| | Forgejo | GitHub |
| --- | --- | --- |
| **Give it** | Repository **Settings, Collaborators**: add the bot with **Write**. For a repository of an organization: add the bot to a team with write access to it | Repository **Settings, Collaborators and teams, Add people**: the bot with the **Write** role, then **accept the invitation as the bot** (sign in as it, or use its e-mail). For an organization: a team with write access, or an outside collaborator |
| **What it can then do** | comment, open tickets, push branches, open pull requests | the same |
| **What it must not have** | no right to push to or merge into the default branch if you can avoid it: protect the default branch so that only a person (after review) can change it. The factory only ever pushes `factory/*` branches | the same: a branch protection rule or ruleset on the default branch that requires a pull request and a review, and lets the bot push `factory/*` |
| **Only for permission levels** | If you use a [`commands` rule](#who-may-start-and-steer-runs) of `write` or `admin`, the bot must be an **administrator of the repository**: Forgejo answers questions about other people's access only to a repository administrator. Without it the answer is "cannot say" and the command is refused. Rules that name people (`none`, `allow`, `deny`) need none of this | Write access is enough; no administrator is needed |

## 5. The webhook

The host calls the factory's api at **`<api address>/webhooks/<name>`** (`<name>` is the host's `name`). The api checks the signature with that host's `webhook_secret`, so a request from anywhere else is answered `401`.

| Field | Forgejo | GitHub |
| --- | --- | --- |
| **Where** | Repository **Settings, Webhooks, Add webhook, Forgejo**. (An organization or the whole site can have webhooks too: the factory reads the repository from each delivery, so one webhook can cover many repositories) | Repository **Settings, Webhooks, Add webhook** (an organization webhook works too, for all its repositories) |
| **URL** | `http://api:8000/webhooks/forgejo` for a Forgejo on the factory's Docker network (the bundled one). For any other Forgejo, an address it can reach, and the address must be allowed by its `webhook.ALLOWED_HOST_LIST` (Forgejo blocks private addresses by default; the bundled one allows `api`) | `https://<your public address>/webhooks/github`. **GitHub cannot reach `localhost` or a private address**: the api needs a public HTTPS address, and a tunnel (for example Cloudflare Tunnel) is enough for a test or a home setup |
| **Method, content type** | `POST`, `application/json` | **`application/json`** (not the default `x-www-form-urlencoded`: the factory cannot read it) |
| **Secret** | the host's `webhook_secret` | the host's `webhook_secret`; leave **SSL verification** enabled |
| **Events** | **Custom events**: **Issues**, **Issue Label**, **Issue Comment**; add **Pull Request Review Rejected** if a review that requests changes should start a [revision](workflows.md#pull-request-revisions). Slash commands on pull requests come as comments, so **Issue Comment** carries them | **Let me select individual events**: **Issues**, **Issue comments**, **Pull request reviews**. Nothing else is read (no pushes, no pull request events) |
| **Active** | yes | yes |
| **Try it** | **Test Delivery** on the webhook: the api answers `202`, and the delivery appears in the Temporal UI (http://localhost:8080) | **Recent Deliveries**: the response code is what the api answered (`202` is good; `401` is a wrong secret or content type, `404` a name that is not in `FACTORY_HOSTS`); **Redeliver** repeats one |

On Forgejo, `python3 tools/onboard-repo.py owner/repo` creates this webhook and the labels in one command (`--host <name>` for another Forgejo; it only knows Forgejo's API). On GitHub both are done by hand.

## 6. The labels

A label starts a workflow when a ticket is opened with it, or when it is added. Create these in the repository (only the first three are needed to start):

`feature`, `bug`, `incident`, then `plan`, `docs`, `quick`, `full`, `needs-architecture` ([what each does](project-setup.md#3-create-the-labels)).

- **Forgejo:** Issues, Labels, or `python3 tools/onboard-repo.py owner/repo`.
- **GitHub:** Issues, Labels, or with the GitHub CLI: `for l in feature bug incident plan docs quick full needs-architecture; do gh label create "$l" --repo owner/repo; done`. GitHub's own **issue types** (a bug, a task) can be mapped to a kind with the host's [`triggers`](configuration.md#factory_hosts) (`item_types`).

## Notifications

The factory needs **none** of the host's notification features. It never reads e-mail or the notification inbox; events reach it only through the webhook.

- **The bot's account:** you can switch off all e-mail and web notifications for it (Forgejo: Settings, Account / Notifications; GitHub: Settings, Notifications), so nothing is sent to its address.
- **People** are notified the way the host always does it: the factory's comments are ordinary comments, so a person who watches a ticket or is mentioned gets the usual message, and a pull request the factory opens notifies the repository's watchers. The bundled Forgejo sends its mail to a local Mailpit inbox (http://localhost:8025), nothing leaves the machine.
- **The factory asks for a review** by requesting the people who reviewed (best effort): that sends them the host's own review-request notification.

## Who may start and steer runs

There are two layers, and the factory-side one is optional.

**On the host** (always in force). The host decides who can do what, and the factory reacts to the events:

| | Forgejo | GitHub |
| --- | --- | --- |
| **Open a ticket, comment** | anyone who can see the repository and is signed in (the bundled Forgejo requires sign-in to view anything and has registration disabled) | on a **public** repository anyone with an account; on a private one, people with access |
| **Add a label** (starts a run) | people with write access | people with **triage** access or more |
| **Review a pull request** | anyone who can see it | anyone on a public repository (a review that asks for changes starts a revision) |

**On the factory** (optional, off by default: anyone who can comment may use commands). The `commands` setting in the host's entry of `FACTORY_HOSTS`, per host and per repository ([all the details](configuration.md#factory_hosts)):

```json
{"name":"github", "...":"...", "commands": {"who": "write", "allow": ["outside-contributor"], "deny": ["mallory"]},
 "repositories": {"owner/public-repo": {"commands": {"who": "none", "allow": ["you", "colleague"]}}}}
```

- `who`: `anyone` (default), `write` or `admin` (what the host says the person may do), or `none` (only the people named in `allow`).
- `allow`: people let in whatever their access; `deny`: people always refused, even administrators; deny wins over allow. Logins are written the way the host writes them (case does not matter).
- `refusal`: `"comment"` (default, one short note that names the rule and never the person) or `"silent"` (public repositories may prefer this).
- The rule covers the slash commands, `/factory <kind>`, answers to an agent, and a review that asks for changes. It does **not** cover a label: the host limits who can label.
- For `write` and `admin` the controller asks the host (the collaborator permission) and keeps the answer for [`FACTORY_PERMISSION_CACHE_SECONDS`](configuration.md#needed-for-the-factory-to-work-but-optional-for-the-stack-to-start) (default 5 minutes). If the host cannot say, the answer is no.
- **Turn it off** by removing `commands` and `repositories` from the host's entry (or setting `"commands":"anyone"`) and recreating the controller: `cd infra && docker compose up -d --force-recreate controller`. A `repositories` entry with `"commands":"anyone"` opens a single repository under a stricter host rule.

## Checking that it works

1. `docker compose logs controller | grep "Git host"` shows each host and its rule.
2. On a repository that is set up, comment `/ask` on any ticket: the factory answers with how to use it (no tokens are spent). No answer: look at the webhook's delivery list (Forgejo: Test Delivery; GitHub: Recent Deliveries) and `docker compose logs api`.
3. Label a ticket `feature` to start a real run.

| Symptom | Likely cause |
| --- | --- |
| The delivery shows `401` | the webhook's secret differs from `webhook_secret`, or (GitHub) the content type is not `application/json` |
| The delivery shows `404` | the name at the end of the URL is not a `name` in `FACTORY_HOSTS` |
| The delivery cannot connect | the host cannot reach the api (GitHub: no public address; Forgejo: the address is not in its `webhook.ALLOWED_HOST_LIST`) |
| Deliveries are `202` but nothing happens | the ticket has no factory label, or the **Issues** / **Issue comments** event is not enabled; or a [`commands` rule](#who-may-start-and-steer-runs) refused the command (look for the note on the ticket) |
| "Resource not accessible by personal access token" / "rejected the comment: 403" | the token lacks a permission, or the repository is not in its selected repositories (GitHub), or the bot is not a collaborator with write access |
| "I could not find out whether you have write access" | the host could not say what the person may do: on Forgejo the bot is not an administrator of the repository; on GitHub the token has no write access. A rule that names people does not need it |
| "The ... host has no token in FACTORY_HOSTS" | the token is empty, or the controller was not recreated after the change |
| Everything stops after some weeks | the token expired (GitHub fine-grained tokens have an expiry date) |

## Disabling and removing

| To stop | Do this |
| --- | --- |
| The factory on **one repository** | Disable or delete its webhook (events stop; a run in progress goes on until it ends or its ticket closes), or remove the bot's access (every call then fails) |
| The factory on **a whole host** | Remove its entry from `FACTORY_HOSTS` and recreate the api and the controller. Its webhooks then get `404`, and a run still in progress for it fails with "No git host named ..." |
| **Restrictions** on who may steer | Remove `commands` and `repositories` from the host's entry (see above) |
| A **leaked or old token** | Revoke it on the host, make a new one, put it into the host entry and recreate the controller. To change the webhook secret, change `webhook_secret` **and** every repository's webhook on that host |
