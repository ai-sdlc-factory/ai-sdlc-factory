# Egress Proxy

[Documentation index](../../docs/README.md) | [Egress allowlist](../../egress/README.md) | [Sandbox launcher](../sandbox-launcher/README.md) | [Architecture](../../docs/architecture.md#trust-boundaries)

The only way out of the disposable agent containers. It is a small forward proxy for **HTTPS tunnels
(`CONNECT`) and nothing else**: it checks the host name against the sandbox's profile in
[`egress/profiles.json`](../../egress/profiles.json), checks that the name really points at a public
address, and then copies bytes. It never looks inside a tunnel.

## How a sandbox is identified

Compose gives each profile its own internal network (no route out, no external DNS) and gives this proxy a
fixed address on each (`10.250.1.2` for `model`, `10.250.2.2` for `packages`, `10.250.3.2` for
`developer`; `SANDBOX_NET_BASE` changes the first two numbers). The proxy knows which profile a
connection belongs to from the address it arrived on (`EGRESS_LISTEN_MAP`). A container can only reach the
proxy address on its own network, so it cannot pick another profile.

## What it refuses

| Request | Answer |
| --- | --- |
| a host not on the profile's list | 403, logged |
| a port other than the allowed ones (443) | 403 |
| an IP address instead of a name | 403 |
| a listed name that resolves to a private, loopback, link-local (cloud metadata), carrier-grade NAT or reserved address | 403 |
| anything but `CONNECT` (plain HTTP is not proxied) | 405, logged with the host the request was aimed at (a tool's plain HTTP requests, such as .NET's certificate revocation checks to `ocsp.digicert.com`, are expected and harmless; the weekly report counts them apart) |
| a malformed or oversized request | 400 / 431 |
| a connection to an address that belongs to no profile | 403 |
| more than 256 open connections | 503 |

Tunnels close after 10 minutes without traffic or 2 hours in total.

## The log

Every decision is one JSON line on standard output:

```bash
docker compose logs -f egress-proxy
```

```json
{"time":"...","profile":"developer","client":"10.250.3.5","host":"pypi.org","port":443,"decision":"allow","reason":"on the allowlist","bytes_up":1204,"bytes_down":88231,"milliseconds":412}
```

`decision` is `allow`, `deny` or `audit` (allowed only because the profile is in audit mode).

With `EGRESS_DENIED_LOG` set (Compose sets it to `/var/log/egress/denied.jsonl`, a volume the controller reads) every **refusal** is also written to that file, one line each in the same shape, at most 10 MB (then it stops writing and says so once on standard error). The factory's [weekly report](../../docs/operations.md#the-weekly-report-what-repeats) reads it to show which hosts sandboxes were refused again and again. A log that cannot be written is reported once and never stops the proxy.

## Running it

Part of the stack: `docker compose up -d egress-proxy`. It is hardened like the other services (read-only
filesystem, no capabilities, no Docker socket, no secrets) and the sandbox launcher refuses to start until
the networks it creates exist. Environment: `EGRESS_PROFILES_FILE`, `EGRESS_LISTEN_MAP`, optional
`EGRESS_PORT` (3128).

## Tests

From the repository root:

```bash
docker build --target test -f services/egress-proxy/Dockerfile -t dev-foundry/egress-proxy-test .
docker run --rm dev-foundry/egress-proxy-test
```

The tests cover the policy (wildcards, ports, IP literals, audit), the address rules, the request parser,
the allowlist file that ships with the factory, and the real proxy against a fake upstream: tunnelling,
refusals, private addresses, timeouts and limits.
