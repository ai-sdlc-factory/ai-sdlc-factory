#!/usr/bin/env python3
"""Sets a Forgejo repository up for the factory: the labels and the webhook (steps 2 and 3 of docs/project-setup.md), and checks .factory/project.json.

    python3 tools/onboard-repo.py owner/repo                   # do it, on the first Forgejo in FACTORY_HOSTS
    python3 tools/onboard-repo.py owner/repo --host codeberg   # on the host of that name
    python3 tools/onboard-repo.py owner/repo --dry-run         # say what would be done, change nothing

It needs an existing repository (the bot's token cannot create one). It reads the host's token and webhook secret from FACTORY_HOSTS in deploy/.env (or the environment) and
never prints either. It only knows Forgejo's API: on GitHub, create the labels and the webhook by hand (docs/operations.md, "Several git hosts"). It is safe to run again: a label or a webhook that is already right is left alone, a webhook with the factory's URL but other events is updated.
"""
import argparse
import base64
import json
import os
import sys
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Name, colour, what it does. The first three are the ones the factory cannot start without.
LABELS = [
    ("feature", "#0e8a16", "starts the feature workflow"),
    ("bug", "#d73a4a", "starts the bug workflow"),
    ("incident", "#b60205", "starts the incident workflow"),
    ("plan", "#5319e7", "splits a big goal into feature tickets for a person to approve"),
    ("docs", "#0075ca", "writes or updates the repository's documentation"),
    ("quick", "#fbca04", "quick mode for this ticket"),
    ("full", "#c5def5", "the full process for this ticket, even if the project defaults to quick"),
    ("needs-architecture", "#d4c5f9", "forces a design review"),
]

# Issues, Issue Label and Issue Comment drive tickets and slash commands (also on pull requests); the review events start a revision; the others are harmless.
EVENTS = ["issues", "issue_label", "issue_comment", "pull_request", "pull_request_comment", "pull_request_review_rejected"]
HOOK_URL = "http://api:8000/webhooks/{host}"  # the API as the git host reaches it; the host is the name in FACTORY_HOSTS


def read_hosts():
    """The hosts of FACTORY_HOSTS, from the environment or deploy/.env (the value is one line of JSON)."""
    text = os.environ.get("FACTORY_HOSTS", "")
    path = os.path.join(ROOT, "deploy", ".env")
    if not text and os.path.exists(path):
        with open(path, encoding="utf-8") as env_file:
            for line in env_file:
                if line.startswith("FACTORY_HOSTS="):
                    text = line.partition("=")[2].strip().strip("'")
    try:
        hosts = json.loads(text)
        if not isinstance(hosts, list) or not hosts:
            raise ValueError
    except ValueError:
        sys.exit("FACTORY_HOSTS must be set in deploy/.env (or the environment) as a JSON list of hosts: see deploy/.env.example.")
    return hosts


class Forgejo:
    def __init__(self, base, token):
        self.base = base.rstrip("/")
        self.token = token

    def call(self, method, path, body=None):
        request = urllib.request.Request(
            f"{self.base}/api/v1/{path}", method=method,
            data=None if body is None else json.dumps(body).encode(),
            headers={"Authorization": f"token {self.token}", "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                text = response.read().decode()
                return response.status, json.loads(text) if text else None
        except urllib.error.HTTPError as error:
            return error.code, None


def ensure_labels(forgejo, repo, dry):
    """Creates the labels the repository lacks. Returns how many were (or would be) created."""
    _, existing = forgejo.call("GET", f"repos/{repo}/labels?limit=200")
    have = {label["name"].lower() for label in existing or []}
    created = 0
    for name, colour, meaning in LABELS:
        if name in have:
            print(f"  label {name}: already there")
            continue
        created += 1
        if dry:
            print(f"  label {name}: would create ({meaning})")
            continue
        status, _ = forgejo.call("POST", f"repos/{repo}/labels", {"name": name, "color": colour, "description": meaning})
        print(f"  label {name}: {'created' if status == 201 else f'FAILED ({status}): the token needs write access to the repository'}")
    return created


def ensure_webhook(forgejo, repo, secret, hook_url, dry):
    """Creates the factory's webhook, or adds the events a webhook with its URL lacks. Returns 1 when something was (or would be) changed, else 0."""
    _, hooks = forgejo.call("GET", f"repos/{repo}/hooks")
    mine = [hook for hook in hooks or [] if hook.get("config", {}).get("url") == hook_url]
    if not mine:
        if dry:
            print(f"  webhook: would create ({hook_url}; events {', '.join(EVENTS)})")
        else:
            wanted = {"type": "forgejo", "active": True, "events": EVENTS, "config": {"url": hook_url, "content_type": "json", "secret": secret}}
            status, _ = forgejo.call("POST", f"repos/{repo}/hooks", wanted)
            print(f"  webhook: {'created' if status == 201 else f'FAILED ({status})'}")
        return 1

    hook = mine[0]
    missing = [event for event in EVENTS if event not in hook.get("events", [])]
    if not missing and hook.get("active", False):
        print("  webhook: already right")
        return 0
    if dry:
        print(f"  webhook: would update (missing events: {', '.join(missing) or 'none'}; active: {hook.get('active')})")
    else:
        events = sorted(set(hook.get("events", [])) | set(EVENTS))
        status, _ = forgejo.call("PATCH", f"repos/{repo}/hooks/{hook['id']}", {"active": True, "events": events})
        print(f"  webhook: {'updated' if status == 200 else f'FAILED ({status})'} (events added: {', '.join(missing) or 'none'})")
    return 1


def check_project_json(forgejo, repo, branch):
    """Says whether .factory/project.json is on the default branch and usable. Changes nothing."""
    status, file = forgejo.call("GET", f"repos/{repo}/contents/.factory/project.json?ref={branch}")
    # An empty repository (no commit yet) answers 200 with a list, not a file object.
    if status != 200 or not isinstance(file, dict) or "content" not in file:
        print(f"  .factory/project.json: MISSING on {branch}. Add one (docs/project-setup.md#4-add-factoryprojectjson, or copy a template from templates/); without it every run stops with a note.")
        return
    try:
        settings = json.loads(base64.b64decode(file["content"]))
        if not isinstance(settings, dict):
            raise ValueError("it is not a JSON object")
    except ValueError as error:
        print(f"  .factory/project.json: NOT USABLE ({error}). Fix it on {branch}.")
        return
    print(f"  .factory/project.json: found, keys: {', '.join(sorted(settings)) or '(empty)'}")
    if not settings.get("gates"):
        print("    note: no `gates` (the project's own checks). The factory still works, but nothing checks a change except the agents.")


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("repository", help="owner/name of an existing repository")
    parser.add_argument("--host", help="the name of the host in FACTORY_HOSTS (default: the first Forgejo)")
    parser.add_argument("--url", help="where this machine reaches the host, when it differs from FACTORY_HOSTS (default: http://localhost:3000 for http://forgejo:3000)")
    parser.add_argument("--dry-run", action="store_true", help="say what would be done and change nothing")
    arguments = parser.parse_args()
    if arguments.repository.count("/") != 1 or not all(arguments.repository.split("/")):
        sys.exit("The repository must be written owner/name.")

    hosts = read_hosts()
    candidates = [h for h in hosts if (h.get("name") == arguments.host if arguments.host else h.get("type", "forgejo") == "forgejo")]
    if not candidates:
        sys.exit(f"No {'host called ' + arguments.host if arguments.host else 'Forgejo'} in FACTORY_HOSTS.")
    host = candidates[0]
    if host.get("type", "forgejo") != "forgejo":
        sys.exit(f"{host.get('name')} is a {host['type']} host: this tool only knows Forgejo. Create the labels and the webhook by hand (docs/operations.md).")
    token, secret = host.get("token", ""), host.get("webhook_secret", "")
    if not token or not secret:
        sys.exit(f"Host {host.get('name')} in FACTORY_HOSTS needs a token and a webhook_secret.")
    url = arguments.url or host.get("url", "")
    if url.rstrip("/") == "http://forgejo:3000":
        url = "http://localhost:3000"  # the factory's own name for the bundled Forgejo is not known to this machine
    forgejo = Forgejo(url, token)
    hook_url = HOOK_URL.format(host=host["name"])
    repo, dry = arguments.repository, arguments.dry_run

    status, info = forgejo.call("GET", f"repos/{repo}")
    if status != 200:
        sys.exit(f"Cannot read {repo} ({status}). It must exist already, and the bot must be able to see it.")
    print(f"{repo}: default branch {info['default_branch']}{' (empty repository)' if info.get('empty') else ''}")

    changed = ensure_labels(forgejo, repo, dry) + ensure_webhook(forgejo, repo, secret, hook_url, dry)
    check_project_json(forgejo, repo, info["default_branch"])

    if not changed:
        print("Nothing to change: the repository is ready.")
    else:
        print(f"{'Would change' if dry else 'Changed'} {changed} thing(s).")


if __name__ == "__main__":
    main()
