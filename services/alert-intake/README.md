# alert-intake

Turns a monitoring alert into a ticket. It receives Alertmanager notifications, decides with **fixed rules (no AI)** whether an alert becomes a ticket, and opens it in Forgejo. If the ticket carries the factory's `incident` label, the factory's [incident workflow](../../docs/workflows.md#incident-workflow) then starts on it, like on any ticket a person wrote.

The design: agents never get production access, and what monitoring says is **data**, never an instruction.

## What it does

| Event | Result |
| --- | --- |
| a **firing** alert whose labels match a route | one ticket in the route's repository, with the route's labels, the alert's details and (optionally) recent log lines. If that route already has an **open ticket opened in the last 15 minutes** (`ALERT_INTAKE_GROUP_MINUTES`), the alert is added to it as a comment instead (`grouped`): one outage often raises several alerts, and each ticket starts an incident run |
| the same alert again while its ticket is open | nothing (the ticket carries a hidden marker with the alert's fingerprint; Alertmanager repeats notifications every few hours) |
| that ticket is closed and the alert fires again | a new ticket |
| a **resolved** alert | a comment on its open ticket ("the alert has resolved, check whether it can be closed"). The ticket is never closed by the intake |
| an alert that matches no route | ignored (logged) |
| more than **6 new tickets in an hour** (setting) | the rest are not opened (logged, answered as `rate_limited`) |
| Forgejo does not answer | the notification is answered with `502`, so Alertmanager sends it again |

The comment on a resolved alert carries the factory's own marker, so the factory never mistakes it for a person's answer.

## Safety rules

- **The route table is the operator's file** (`ALERT_INTAKE_MAPPING_FILE`). An alert chooses nothing: not the repository, not the labels, not the log queries. An alert with a label such as `repository: victim/repo` is just a label.
- **Bearer token.** `POST /alerts` needs `Authorization: Bearer <ALERT_INTAKE_TOKEN>` (compared in constant time). Alertmanager reads the token from a file, so it is never written into its config.
- **Bounded input.** The body is limited (`ALERT_INTAKE_MAX_BODY_BYTES`, default 256 KB, also when the length is not announced), at most 50 alerts per notification, strict parsing.
- **Redacted and inert text.** Everything that goes into a ticket (titles, annotations, labels, log lines) is cut to a fixed length, secrets are masked (`password=...`, bearer tokens, credentials in URLs, long opaque tokens), e-mail addresses are masked, `@` mentions cannot notify anyone, and a backtick can never close a code block. The ticket itself says that its content is data, not instructions.
- **A dry run is the default.** With `ALERT_INTAKE_DRY_RUN=true` nothing is written to Forgejo and Forgejo is not even asked: the ticket that would be opened is logged. The Forgejo token is then not needed.
- **Least privilege.** In the demo stack the Forgejo token is given to the service only when tickets are really wanted.

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
- `repository`: `owner/name`. `labels`: at most 5 ticket labels. A label the repository does not have yet is created.
- `evidence`: at most 3 Loki queries, run when a ticket is opened (needs `ALERT_INTAKE_LOKI_URL`). Each adds at most 15 lines of the last 15 minutes, each at most 200 characters, 3,000 characters in all, in a code block. A log source that fails never stops the ticket.

## Metrics

`GET /metrics` (no token; the port is not published outside the Compose network) gives Prometheus counters only, never text from an alert: `alert_intake_notifications_total`, `alert_intake_alerts_total`, `alert_intake_outcomes_total{action}` (`created`, `duplicate`, `commented`, `grouped`, `rate_limited`, `ignored`, `dry_run`, `failed`), `alert_intake_rejected_total{reason}` (`unauthorized`, `too_large`, `invalid`) and the gauges `alert_intake_dry_run`, `alert_intake_routes` and `alert_intake_max_tickets_per_hour`. Every series exists from the start, so a rate works before the first event. The demo's Overview dashboard and its monitoring alerts use them.

## Settings

See [the configuration reference](../../docs/configuration.md#alert-intake). Run the tests with
`docker build --target test -f services/alert-intake/Dockerfile -t t . && docker run --rm t`.

The service is used by the demo environment in [`demo/`](../../demo/README.md).
