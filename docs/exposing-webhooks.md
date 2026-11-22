# Letting GitHub reach the factory

[Documentation index](README.md) | [Connecting a git host](git-hosts.md#5-the-webhook) | [Getting started](getting-started.md)

A git host tells the factory about new tickets and comments by calling its api: `POST /webhooks/<host name>` on port 8000. A Forgejo on the same Docker network reaches it directly. **GitHub cannot reach `localhost` or a private address**, so the api needs a public HTTPS address, or a tunnel to it. This page compares the ways to get one and shows how to make sure only GitHub can use it.

> **Status.** The factory's side (the route, the signature check, the answers listed below) was checked against a running stack. The tunnels and proxies on this page were **not** run end to end by the authors: the commands and settings come from the providers' documentation, so read their current pages too, and expect to adjust.

## What has to be public

Only one thing: **`POST /webhooks/<host name>`**. GitHub sends nothing else. Do **not** expose the rest of the api (`/health/*`, the OpenAPI document), the Temporal UI (8080), pgAdmin (5050), PostgreSQL or Forgejo.

The api protects that route by itself, whatever sits in front of it:

| Request | Answer |
| --- | --- |
| valid signature, event header, delivery id and a JSON object up to 1 MiB | `202` (or `200` for a repeated delivery) |
| no signature, or one that does not match the host's `webhook_secret` | `401` |
| a name that is not a `name` in `FACTORY_HOSTS` | `404` |
| any method but `POST` | `405` |

So a stranger who finds the address can only collect `401`s: without the secret a request is never accepted. Everything below that limits who may even try is a second layer.

## Ways to get an address

| Way | Address | Permanent? | Needs | Notes |
| --- | --- | --- | --- | --- |
| **Cloudflare Tunnel, named** | a hostname you choose, such as `factory.example.org` | yes: the tunnel is a record in your Cloudflare account and survives restarts | a Cloudflare account and a domain whose DNS is on Cloudflare (a cheap one is enough) | outbound connection only, so no open port or public IP; custom firewall rules at the edge ([below](#only-github-may-send)) |
| Cloudflare quick tunnel | a random `*.trycloudflare.com` name | **no**: new at every start, no uptime guarantee | nothing (no account) | for a first try only; you would have to edit the webhook each time |
| Tailscale Funnel | a stable `<machine>.<tailnet>.ts.net` name | yes | a Tailscale account; Funnel enabled for the machine | no domain needed; check Tailscale's current limits and ports |
| ngrok | a stable name on the free plan, a custom one on paid plans | yes (see notes) | an ngrok account | the free plan has usage caps; sources disagree on what it includes, so read ngrok's current page |
| **Your own server** with Caddy or nginx | your domain | yes | a machine with a public address, a domain, TLS (Caddy gets certificates by itself) | most control; the factory can run on that machine or reach it over a VPN |
| `gh webhook forward` (the GitHub CLI extension `cli/gh-webhook`) | none: GitHub forwards events to a local URL while the command runs | no | the GitHub CLI and admin rights on the repository | **development only**; nothing is delivered while it is not running |

Two cautions that apply to every provider in the middle:

- **They see your traffic.** A webhook carries ticket text and code excerpts, and a tunnel or relay terminates TLS. Pick one you trust with that, and do not use a public relay such as smee.io for private repositories.
- **They must not change the body.** The signature is computed over the exact bytes. A proxy that rewrites or recompresses the payload turns every delivery into a `401`.

Our pick for a permanent setup at home or on a laptop is a **named Cloudflare Tunnel**: the machine only makes outbound connections, and the edge can filter by address. A **VPS with Caddy** is the pick if you already run a server.

## Cloudflare Tunnel, step by step

You need a Cloudflare account and a domain with its DNS on Cloudflare. Install `cloudflared` on the machine that runs the factory (on the host, not in Docker: the api listens on `127.0.0.1:8000`).

```bash
cloudflared tunnel login                                    # opens a browser; picks the domain
cloudflared tunnel create ai-sdlc-factory                   # prints the tunnel id and writes its credentials file
cloudflared tunnel route dns ai-sdlc-factory factory.example.org
```

`~/.cloudflared/config.yml`: expose **only** the webhook path, answer `404` to everything else.

```yaml
tunnel: <the tunnel id>
credentials-file: /home/you/.cloudflared/<the tunnel id>.json
ingress:
  - hostname: factory.example.org
    path: ^/webhooks/
    service: http://localhost:8000
  - service: http_status:404
```

Run it as a service so it survives a reboot (`cloudflared service install`, then start it with your system's service manager). On Windows with WSL, install it as a Windows service or run it inside WSL with systemd enabled; if the tunnel is down, Cloudflare answers with a `5xx` and GitHub shows the delivery as failed.

Check it from another machine:

```bash
curl -i https://factory.example.org/health/ready                 # 404: not exposed
curl -i -X POST https://factory.example.org/webhooks/github      # 401: reached the api, which rejects an unsigned request
```

Then point the webhook at `https://factory.example.org/webhooks/github` ([the webhook settings](git-hosts.md#5-the-webhook)), or let the tool do it: `python3 tools/onboard-repo.py owner/repo --host github --public-url https://factory.example.org`. A tunnel made in Cloudflare's dashboard (Zero Trust, Networks, Tunnels) works the same way with a public hostname for `http://localhost:8000`; the file above is the way to limit it to `/webhooks/`.

## Only GitHub may send

Three layers, from the strongest:

1. **The signature** (always on): without the `webhook_secret` nothing is accepted. Keep the secret to yourself, and make it long (`openssl rand -hex 32`).
2. **The path and the method**: the tunnel file above exposes `/webhooks/` and nothing else, and GitHub only ever `POST`s.
3. **The address**: GitHub publishes where webhooks come from (the `hooks` list of `https://api.github.com/meta`). A rule at your edge can refuse everyone else, so strangers never reach the api. The list **changes** (GitHub added ranges in October 2026) and GitHub says it is **not guaranteed to be exhaustive**, so keep it current and treat it as noise reduction, not as the lock. If you forget, deliveries start failing with a `403` from your edge, visible in GitHub's *Recent Deliveries*.

[`tools/github-hook-ips.py`](../tools/github-hook-ips.py) fetches the current list and prints it in the form you need (it sends no credentials):

```bash
python3 tools/github-hook-ips.py --format cloudflare    # a Cloudflare rule expression
python3 tools/github-hook-ips.py --format caddy         # for Caddy's remote_ip
python3 tools/github-hook-ips.py --format nginx         # allow lines, ending with deny all
python3 tools/github-hook-ips.py --url https://github.example.com/api/v3   # GitHub Enterprise Server
```

**On Cloudflare:** Security, WAF, Custom rules, Create rule, *Edit expression*, paste the output of `--format cloudflare`, action **Block**. It blocks a request to `/webhooks/` from any address that is not GitHub's. Check Cloudflare's current limits on rules and expression length for your plan.

**On your own server with Caddy** (untested here; `handle` blocks keep the order clear):

```
factory.example.org {
    @github {
        path /webhooks/*
        method POST
        remote_ip <output of --format caddy>
    }
    handle @github {
        reverse_proxy localhost:8000
    }
    handle {
        respond 404
    }
}
```

Behind a CDN or tunnel the proxy sees the CDN's addresses, not GitHub's, so put the address rule at the outermost layer (for Cloudflare, the WAF rule above).

## Checking a delivery

On GitHub: the repository, *Settings*, *Webhooks*, your webhook, *Recent Deliveries* (and *Redeliver* to try again).

| Response | Meaning |
| --- | --- |
| `202` | accepted; the run shows in the Temporal UI (http://localhost:8080) |
| `401` | wrong secret, or the content type is not `application/json`, or a proxy changed the body |
| `404` | the name at the end of the URL is not a `name` in `FACTORY_HOSTS`, or the tunnel file does not expose that path |
| `403` | your address rule blocked it: refresh the list (see above) |
| `5xx` from the tunnel or proxy | it cannot reach the api: is the tunnel running, is the factory up (`/health/ready` locally)? |
