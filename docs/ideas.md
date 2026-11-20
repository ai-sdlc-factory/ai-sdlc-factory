# Ideas for later

[Documentation index](README.md) | [Operations](operations.md)

One thing that would change real outcomes, picked after the live tests of 2026-11-15. It is not built. Each says what problem it solves, how it would work in this codebase, what it must not do, and how to know it is done. They follow the rules in [AGENTS.md](../AGENTS.md): **the controller decides, agents advise**; a new setting needs parsing, tests, a place in `ProjectKeys.cs`, an entry in [project-setup.md](project-setup.md) and, with a factory default, a `FACTORY_DEFAULT_*` variable; a new role needs a role file, a row in [configuration.md](configuration.md), a prompt builder and a parser. Delete an idea from this file when it is built.

| # | Idea | Solves | Size |
| --- | --- | --- | --- |
| 1 | [Other git hosts, in parallel](#1-other-git-hosts-in-parallel) | the factory only works with Forgejo | medium refactor, then days per host |

Left out on purpose, for now: auto-triage of unlabelled tickets and a permission check on commands (needed the day a second person uses the factory).

## 1. Other git hosts, in parallel

**Problem.** Everything the factory reads from or writes to a repository goes through Forgejo. A team on GitHub, GitLab, Azure DevOps or Bitbucket cannot use it, and one factory cannot serve repositories on two hosts.

**Where Forgejo is built in** (measured 2026-11-15):
- Controller: 33 files, about 140 mentions. `ForgejoClient` has 17 operations (comments, issues, pull requests and their files, reviews, review requests, branches, a repository list), wrapped by `GitHostActivities`. `EventRouter` reads Forgejo's webhook JSON directly (`issues`, `issue_comment`, `pull_request_rejected`, labels, `is_pull`).
- API service: `WebhookEndpoints` and `WebhookVerifier` check Forgejo's HMAC-SHA256 signature on one fixed route.
- Alert intake: its own small client (`Forgejo.cs`: find a ticket by marker, create, comment, add a marker).
- Identity: `owner/repo` is the key everywhere (`WorkIds.For` hashes it into workflow ids and workspace folders, the usage log, the janitor's prefixes, the weekly report). There is **no host** in any of it.
- Not affected: the agents, sandboxes, workflows, gates, QA, reviewers, token limits, and cloning and pushing (plain git over HTTPS with a token).

**How.** In this order, each step keeping every test green. **Built so far: steps 1, 2 and 3** (the `IGitHost` port, the `RepoEvent` and the webhook adapters, and the host in the identity with `FACTORY_EXTRA_HOSTS`; see [Several git hosts](operations.md#several-git-hosts)). Steps 4 to 6 are still to do.
1. **A host port.** Turn the operations of `ForgejoClient` into an interface (`IGitHost`) with Forgejo as the only implementation, and have `GitHostActivities` call the interface. No behaviour change.
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
