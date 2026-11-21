# Forgejo, a git host to try the factory on

A Forgejo that runs as a project of its own. The factory ([`../../infra`](../../infra/README.md)) does not need it and knows nothing about it: it serves the hosts its `FACTORY_HOSTS` lists, and this one is listed there like any other (`"url":"http://forgejo:3000"`). Use another host (GitHub, a Forgejo you already have) and you can leave this folder alone.

## Start

Start the factory first: this project joins two of its networks, `hooks` (Forgejo to the factory's api, for webhooks) and `forge` (the factory's controller to Forgejo, for its API and git).

```bash
cd companions/forgejo
cp -n .env.example .env
docker compose up -d
```

Forgejo is then at http://localhost:3000 (SSH: port `2222`); finish the installation there and create an **administrator** account. Registration is disabled, and sign-in is required to view anything. It keeps its data in SQLite in the volume `forgejo-data`. [Mailpit](http://localhost:8025) catches the emails Forgejo sends (nothing is delivered externally).

| Setting (`.env`) | What | Default |
| --- | --- | --- |
| `FORGEJO_HTTP_PORT`, `FORGEJO_SSH_PORT` | Forgejo's web and SSH ports on this machine (127.0.0.1 only) | `3000`, `2222` |
| `MAILPIT_UI_PORT`, `MAILPIT_SMTP_PORT` | Mailpit's inbox and SMTP ports | `8025`, `1025` |
| `FACTORY_PROJECT` | the factory's Compose project name, if it is not `dev-foundry` (it prefixes the networks this project joins) | `dev-foundry` |
| `FORGEJO_DB_PASSWORD` | only with `compose.shared-postgres.yaml` (below): the password of the `forgejo` role in the factory's PostgreSQL | none |

## The factory bot account

The factory comments, pushes branches and opens pull requests as a user of its own. Create it once (the password is random and not needed afterwards); the second command prints the token:

```bash
docker compose exec -u 1000 forgejo forgejo admin user create \
  --username factory-bot --email factory-bot@dev-foundry.test \
  --random-password --must-change-password=false
docker compose exec -u 1000 forgejo forgejo admin user generate-access-token \
  --username factory-bot --token-name controller --scopes "write:issue,write:repository" --raw
```

Put the token into the `"token"` of this host in `FACTORY_HOSTS` in `infra/.env` (with `"url":"http://forgejo:3000"` and a `webhook_secret`; see [Configuration](../../docs/configuration.md#factory_hosts)) and recreate the controller. Then [connect each repository](../../docs/project-setup.md).

## A Forgejo that started in the factory's stack

Before this folder existed, Forgejo ran inside the factory's Compose project and kept its database in the factory's PostgreSQL and its files in the volume `dev-foundry_forgejo-data`. To carry on with exactly that data, nothing moved:

```bash
# 1. stop and remove the old containers (named for the factory's Compose project; the data stays in the volume and the database)
docker rm -f dev-foundry-forgejo-1 dev-foundry-mailpit-1
# 2. here: put the old FORGEJO_DB_PASSWORD (the password of the "forgejo" role) into .env, then
cd ../companions/forgejo && docker compose -f compose.yaml -f compose.shared-postgres.yaml up -d
```

Use both `-f` files for every later command too. `compose.shared-postgres.yaml` points Forgejo at that PostgreSQL (over the factory's `data` network) and at that volume. A fresh install does not need it.
