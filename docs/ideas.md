# Ideas for later

[Documentation index](README.md) | [Operations](operations.md)

What is left to build, as designs and not promises. Each says what problem it solves, how it would work in this codebase, what it must not do, and how to know it is done. They follow the rules in [AGENTS.md](../AGENTS.md): **the controller decides, agents advise**; a new setting needs parsing, tests, a place in `ProjectKeys.cs` (for a `project.json` key), an entry in [project-setup.md](project-setup.md) or [configuration.md](configuration.md) and, with a factory default, a `FACTORY_DEFAULT_*` variable; a new role needs a role file, a row in [configuration.md](configuration.md), a prompt builder and a parser. Delete an idea from this file when it is built.

| # | Idea | Solves | Size |
| --- | --- | --- | --- |
| 1 | [More git hosts](#1-more-git-hosts) | the factory serves Forgejo and GitHub only | days per host |

Left out on purpose, for now: auto-triage of unlabelled tickets (an agent that reads a ticket with no factory label and proposes one). Today a ticket without a label is ignored, which is the safe default.

Also left out: teams in the rules of who may steer the factory (`"team": "org/team"` next to `allow`). The access levels (`write`, `admin`) already cover most teams, because a team's members get their access from it; team lookups only exist for organizations and need an extra token permission on each host (GitHub: Members read; Forgejo: `read:organization`), so they widen the bot's token for a convenience, and they could not be checked live without an organization on each host. If a long `allow` list going stale becomes the real problem, reading the list from a file is the cheaper fix.

## Built, and still to check live

The factory serves several git hosts at once: Forgejo and GitHub, listed in `FACTORY_HOSTS` (the port, the neutral event, the host in the identity, per-host triggers, a client and a webhook adapter per kind; see [Several git hosts](operations.md#several-git-hosts)). It was run live on GitHub with a feature ticket, `/ask`, `/setup`, `/fix`, `/fix qa`, `/update` and `/qa`. One thing was not: a review that **requests changes** starting a revision on GitHub, because a pull request's author cannot request changes on it and the factory ignores reviews by its own account, so it needs a second GitHub account. It is covered by unit tests and a workflow scenario only.

Who may steer the factory is built too: a host (and single repositories of it) can restrict commands to people with write or admin access, or to named people, with a rule on top for single commands and an opt-in tightening in the repository's own project file ([`commands` in `FACTORY_HOSTS`](configuration.md#factory_hosts), with the setup of both hosts in [Connecting a git host](git-hosts.md)). It was checked live on GitHub and Forgejo (levels, deny and allow lists, silent refusal, per-repository exceptions, a rule for one command); the reviewer-of-a-review case, and the project file's own `commands` key (it needs a merge into a default branch), are covered by tests and scenarios, not live.

## 1. More git hosts

**Problem.** A team on GitLab, Azure DevOps or Bitbucket cannot use the factory. Everything else is host-neutral by now (the [port, the events and the identity](operations.md#several-git-hosts)), so a host is one client, one webhook adapter, a few settings and a live check.

**How.** The recipe is the rule "a new kind of git host" in [AGENTS.md](../AGENTS.md): a webhook adapter in `src/contracts/Hosts/` (with tests of its payloads and its signature or secret), an `IGitHost` client in `GitHost/<Kind>/` and one entry in `GitHostKinds` (a test checks that every adapter has one), a ticket client on `RestTicketHost` and an entry in `TicketHostKinds` in the [alert intake](https://github.com/OWNER/dev-foundry-alert-intake) (its own repository) if alerts should open tickets there, a column in [git-hosts.md](git-hosts.md) (the bot account, the token scopes, the webhook and its events, the permissions) and a row in the limitations of [operations.md](operations.md) if it differs, and a live run on a real repository (the first GitHub run found two bugs that fakes could not).

| Host | Effort | Difficulty |
| --- | --- | --- |
| GitLab | 4-7 days | Merge requests and notes; no "request changes" (approvals and resolved threads instead); the webhook carries a secret token, not an HMAC. Can run locally (GitLab CE in Docker), so it can be tested without an account. |
| Azure DevOps | 5-8 days | Work items and pull requests are separate things; tags instead of labels; comments are threads; the review state is a vote; PAT auth and service hooks. |
| Bitbucket Cloud | 5-8 days | The built-in issue tracker has no labels (and is often replaced by Jira), so triggers must come from pull request comments (the `/factory <kind>` command exists for that); Bitbucket Server is a different API again. |

**Must not.** Let a host decide anything (hosts only deliver events and carry results); treat a fork's pull request as trusted on a host where forks work differently (the review-reads-default-branch rule must hold on every host); lose the hidden `<!-- dev-foundry:factory -->` marker (check that the host renders HTML comments invisibly, or use another reliable way to recognise the bot's own comments, such as its user id); assume an identical set of review states.

**Done when.** The new host runs the same path live (a feature ticket, `/ask`, a revision after a review, `/qa`) and its pull request is reviewed and merged by a person; it runs next to the others on one controller without sharing a workspace or a usage count; [git-hosts.md](git-hosts.md) names its trigger setting, token scopes and webhook events. The [commands rule](configuration.md#factory_hosts) (who may steer the factory) has to work on it too: an operation for a person's access on the host, or an honest "cannot say".
