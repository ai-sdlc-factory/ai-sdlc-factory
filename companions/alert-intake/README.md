# alert-intake

Turns a monitoring alert into a ticket. It receives Alertmanager notifications, decides with **fixed rules (no AI)** whether an alert becomes a ticket, and opens it on a git host (Forgejo or GitHub). If the ticket carries the factory's `incident` label, the factory's [incident workflow](../../docs/workflows.md#incident-workflow) then starts on it, like on any ticket a person wrote.

The design: agents never get production access, and what monitoring says is **data**, never an instruction.

## What it does

| Event | Result |
| --- | --- |
| a **firing** alert whose labels match a route | one ticket in the route's repository, with the route's labels, the alert's details and (optionally) recent log lines. If that route already has an **open ticket opened in the last 15 minutes** (`ALERT_INTAKE_GROUP_MINUTES`), the alert is added to it as a comment instead (`grouped`): one outage often raises several alerts, and each ticket starts an incident run |
| the same alert again while its ticket is open | nothing (the ticket carries a hidden marker with the alert's fingerprint; Alertmanager repeats notifications every few hours) |
| that ticket is closed and the alert fires again | a new ticket |
| the same alert (or another of the same route) again within **two minutes** of a ticket the intake itself opened | the same as above (duplicate, or grouped): the intake remembers its own tickets for two minutes in memory, because a host such as GitHub lists a new ticket only after a few seconds. A ticket closed within those two minutes is still counted as open for them |
| a **resolved** alert | a comment on its open ticket ("the alert has resolved, check whether it can be closed"). The ticket is never closed by the intake |
| an alert that matches no route | ignored (logged) |
| more than **6 new tickets in an hour** (setting) | the rest are not opened (logged, answered as `rate_limited`) |
| the git host does not answer | the notification is answered with `502`, so Alertmanager sends it again |

The comment on a resolved alert carries the factory's own marker, so the factory never mistakes it for a person's answer.

## Safety rules

- **The route table is the operator's file** (`ALERT_INTAKE_MAPPING_FILE`). An alert chooses nothing: not the repository, not the labels, not the log queries. An alert with a label such as `repository: victim/repo` is just a label.
- **Bearer token.** `POST /alerts` needs `Authorization: Bearer <ALERT_INTAKE_TOKEN>` (compared in constant time). Alertmanager reads the token from a file, so it is never written into its config.
- **Bounded input.** The body is limited (`ALERT_INTAKE_MAX_BODY_BYTES`, default 256 KB, also when the length is not announced), at most 50 alerts per notification, strict parsing.
- **Redacted and inert text.** Everything that goes into a ticket (titles, annotations, labels, log lines) is cut to a fixed length, secrets are masked (`password=...`, bearer tokens, credentials in URLs, long opaque tokens), e-mail addresses are masked, `@` mentions cannot notify anyone, and a backtick can never close a code block. The ticket itself says that its content is data, not instructions.
- **A dry run is the default.** With `ALERT_INTAKE_DRY_RUN=true` nothing is written to a git host and none is even asked: the ticket that would be opened is logged. The tokens are then not needed.
- **Least privilege.** The intake has its own list of hosts and tokens (`ALERT_INTAKE_HOSTS`), not the factory's; in the demo stack the tokens are given to it only when tickets are really wanted.

## Routes

```json
{
  "routes": [
    {
      "match": { "service": "weather" },
      "repository": "valters/weather",
      "labels": ["incident"],
      "evidence": [
        { "title": "Recent error lines of the API", "query": "{compose_service=\"weather-api\"} |~ \"(?i)(error|exception|fatal)\"" }
      ]
    }
  ]
}
```

- `match`: 1 to 10 alert labels that must all be equal. The first matching route wins. At most 50 routes.
- `repository`: `owner/name`. `host` (optional): the name of a git host from `ALERT_INTAKE_HOSTS` where the ticket opens; without it, the first host of that list. `labels`: at most 5 ticket labels. A label the repository does not have yet is created.
- `evidence`: at most 3 Loki queries, run when a ticket is opened (needs `ALERT_INTAKE_LOKI_URL`). Each adds at most 15 lines of the last 15 minutes, each at most 200 characters, 3,000 characters in all, in a code block. A log source that fails never stops the ticket.

## Metrics

`GET /metrics` (no token; the port is not published outside the Compose network) gives Prometheus counters only, never text from an alert: `alert_intake_notifications_total`, `alert_intake_alerts_total`, `alert_intake_outcomes_total{action}` (`created`, `duplicate`, `commented`, `grouped`, `rate_limited`, `ignored`, `dry_run`, `failed`), `alert_intake_rejected_total{reason}` (`unauthorized`, `too_large`, `invalid`) and the gauges `alert_intake_dry_run`, `alert_intake_routes` and `alert_intake_max_tickets_per_hour`. Every series exists from the start, so a rate works before the first event. The demo's Overview dashboard and its monitoring alerts use them.

## Settings

It stands on its own: it shares no code and no settings with the factory. It only creates tickets; a factory (or a person) picks them up from the host.

| Variable | Default | Meaning |
| --- | --- | --- |
| `ALERT_INTAKE_TOKEN` | none (required, 16+ characters) | the bearer token Alertmanager sends with every notification |
| `ALERT_INTAKE_HOSTS` | none (required) | the git hosts a route may open tickets on, as one line of JSON: `[{"name":"forgejo","type":"forgejo","url":"http://forgejo:3000","token":"..."},{"name":"github","type":"github","url":"https://github.com","token":"..."}]`. `name` (lower case letters, digits and `-`) is what a route's `host` says; `type` is `forgejo` (the default; Gitea also works) or `github` (`url` is `https://github.com` or a GitHub Enterprise Server's address); `url` is where the host is, as the intake reaches it. A route without `host` uses the first. A token is required on every host unless `ALERT_INTAKE_DRY_RUN` is `true`. A setting that cannot be used stops the start with a message that names the host and never prints a token |
| `ALERT_INTAKE_DRY_RUN` | `true` | `true` writes nothing to a git host and only logs the ticket that would be opened |
| `ALERT_INTAKE_MAPPING_FILE` | `/etc/alert-intake/mapping.json` | the operator's route table: which alert goes to which repository, with which labels and log queries |
| `ALERT_INTAKE_MAX_TICKETS_PER_HOUR` | `6` | at most this many new tickets in any hour (1 to 100) |
| `ALERT_INTAKE_GROUP_MINUTES` | `15` | an alert of a route that already has an **open ticket opened in the last this-many minutes** is added to that ticket as a comment instead of opening a second one, so one outage is one ticket and one incident run (0 switches it off, up to 1440). Alerts of different routes are never grouped |
| `ALERT_INTAKE_MAX_BODY_BYTES` | `262144` | largest accepted notification (1,024 to 1,048,576) |
| `ALERT_INTAKE_LOKI_URL` | empty | where Loki answers, for the log lines a route adds to its tickets; empty means no log evidence |

**Tokens.** Forgejo: a token with `write:issue` (and `write:repository` to create labels). GitHub: a fine-grained token with **Issues** read and write on the repositories of the routes. GitHub creates a label a ticket names if the repository has not got it.

## Build and test

It builds from this folder, with its own `Directory.Build.props` and `Directory.Packages.props`:

```bash
docker build --target test -t alert-intake-test companions/alert-intake && docker run --rm alert-intake-test
docker build --target runtime -t alert-intake companions/alert-intake
```

The service is used by the demo environment in [`demo/`](../demo/README.md), which writes its settings and runs it next to a monitoring stack.
