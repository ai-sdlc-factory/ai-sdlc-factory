# Egress allowlist

What the disposable agent containers may reach on the internet. A sandbox has **no route out** and no
external DNS; the only thing it can talk to is an [egress proxy](../services/egress-proxy/README.md) that
tunnels HTTPS to the hosts listed here and refuses everything else. The file is trusted configuration:
only a person editing it can add a host. A repository's `.factory/project.json` cannot, because that
would let repository content widen what an agent can reach.

## Profiles

A run asks the sandbox launcher for one profile (`network` in the request); no profile means no network
at all. The factory picks the profile by role, never the agent or the repository:

| Profile | Hosts | Used by |
| --- | --- | --- |
| `none` (no network) | nothing | the evidence check |
| `model` | `chatgpt.com`, `auth.openai.com` | analyst, coverage, architect, reviewers, QA |
| `packages` | package registries (PyPI, npm, NuGet, Maven, crates.io, Go, RubyGems) | the project's own checks |
| `developer` | `model` + `packages` | the developer |

Each profile has its own internal Docker network and its own proxy address, so a container cannot ask
for a more generous profile: from the `model` network the `developer` proxy is not reachable at all.

## The file

[`profiles.json`](profiles.json):

```json
{
  "ports": [443],
  "profiles": {
    "model": { "hosts": ["chatgpt.com", "auth.openai.com"] },
    "developer": { "include": ["model", "packages"] }
  }
}
```

- `ports`: the only ports a tunnel may go to (443).
- `hosts`: exact names, or `*.example.com` for any subdomain (not the bare domain). Plain names only: IP
  addresses are refused.
- `include`: add the hosts of other profiles (one level).
- `mode: "audit"` (see below) allows every host and only logs it.

Changes take effect after `docker compose restart egress-proxy`. The proxy refuses to start on a file
it cannot parse, and a unit test checks the shipped file: the `model` profile must stay small and free of
registries, and no profile may allow a broad host such as `github.com` or `*.amazonaws.com`.

## Adding a host

1. Run the thing that needs it and read what the proxy refused:
   `docker compose logs egress-proxy | grep '"decision":"deny"'`.
2. If the host is a real dependency of a tool you want, add it to the narrowest profile (`packages` for
   a registry, `model` only for the model provider).
3. Restart the proxy. To find out what an unfamiliar tool needs, temporarily give a *copy* of a profile
   `"mode": "audit"` and read the log; never leave it on (the proxy prints a loud warning at start).

## What this does and does not protect

It stops an agent that is tricked (by text in a ticket, a repository or a web page) from sending code or
the OpenCode login to an arbitrary server, from reaching your network or the host, and from downloading
and running things from arbitrary places. Direct connections fail ("Network is unreachable"), DNS does
not resolve, and the proxy also refuses names that point at private, loopback or link-local addresses.

It does not stop data from going to a host that **is** allowed. The model provider receives whatever is
in a prompt, which is inherent, and a package registry accepts requests from anyone. It only sees host
names (the tunnels are encrypted), so it cannot tell a download from an upload.

Known behaviour: OpenCode tries to install a few helper packages from the npm registry every time it
starts. They are not needed (a run works with the registry blocked) and are not executed. In the `model`
profile they are not even attempted (npm is pointed at a dead address). The `developer` profile allows the
npm registry because projects install from it, so there OpenCode's own downloads go through as well.
Remove `registry.npmjs.org` from `packages` if none of your projects use npm.
