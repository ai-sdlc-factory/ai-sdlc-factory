#!/usr/bin/env python3
"""Prints the addresses GitHub sends webhooks from, in the form a firewall or proxy wants, so only GitHub can reach the factory's webhook route.

    python3 tools/github-hook-ips.py                       # one range per line
    python3 tools/github-hook-ips.py --format cloudflare   # a Cloudflare custom rule expression: block the webhook path for everyone else
    python3 tools/github-hook-ips.py --format caddy        # for a remote_ip matcher
    python3 tools/github-hook-ips.py --format nginx        # allow lines, ending with deny all
    python3 tools/github-hook-ips.py --url https://github.example.com/api/v3   # GitHub Enterprise Server

The ranges come from GitHub's meta endpoint (the "hooks" list), because they change: run this again when deliveries start failing with a 403 from your firewall, and
subscribe to GitHub's changelog. GitHub says the list is not guaranteed to be exhaustive, so treat an allowlist as a second layer: the factory's api already rejects every
request whose signature (HMAC-SHA256 with the host's webhook_secret) does not match. Standard library only; it sends no secret and no token.
"""
import argparse
import json
import sys
import urllib.error
import urllib.request


def hook_ranges(base):
    """The "hooks" ranges of the meta endpoint at base (https://api.github.com, or https://<host>/api/v3)."""
    request = urllib.request.Request(base.rstrip("/") + "/meta", headers={"Accept": "application/vnd.github+json", "User-Agent": "ai-sdlc-factory-hook-ips"})
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            meta = json.load(response)
    except (urllib.error.URLError, ValueError) as error:
        sys.exit(f"Could not read {base}/meta: {error}")
    ranges = meta.get("hooks") if isinstance(meta, dict) else None
    if not isinstance(ranges, list) or not ranges or not all(isinstance(item, str) for item in ranges):
        sys.exit(f"{base}/meta has no list of webhook addresses (\"hooks\"). On GitHub Enterprise Server it needs an administrator's view of the instance's addresses.")
    return ranges


def render(ranges, form, path):
    if form == "lines":
        return "\n".join(ranges)
    if form == "caddy":
        return " ".join(ranges)
    if form == "nginx":
        return "\n".join([f"allow {item};" for item in ranges] + ["deny all;"])
    # Cloudflare custom rule, action Block: a request to the webhook path from any address that is not GitHub's.
    return f'(starts_with(http.request.uri.path, "{path}") and not ip.src in {{{" ".join(ranges)}}})'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--format", choices=["lines", "cloudflare", "caddy", "nginx"], default="lines")
    parser.add_argument("--url", default="https://api.github.com", help="the API address (default https://api.github.com; GitHub Enterprise Server: https://<host>/api/v3)")
    parser.add_argument("--path", default="/webhooks/", help="the webhook path the Cloudflare rule protects (default /webhooks/)")
    arguments = parser.parse_args(argv)
    print(render(hook_ranges(arguments.url), arguments.format, arguments.path))


if __name__ == "__main__":
    main()
