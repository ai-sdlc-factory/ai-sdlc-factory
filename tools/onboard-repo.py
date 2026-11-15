#!/usr/bin/env python3
"""Sets a Forgejo repository up for the factory: the labels and the webhook (steps 2 and 3 of docs/project-setup.md), and checks .factory/project.json.

    python3 tools/onboard-repo.py owner/repo            # do it
    python3 tools/onboard-repo.py owner/repo --dry-run  # say what would be done, change nothing

It needs an existing repository (the bot's token cannot create one). It reads FORGEJO_BOT_TOKEN and FORGEJO_WEBHOOK_SECRET from infra/.env (or the environment) and
never prints either. It is safe to run again: a label or a webhook that is already right is left alone, a webhook with the factory's URL but other events is updated.
"""
import argparse
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
HOOK_URL = "http://api:8000/webhooks/forgejo"


def read_env():
    values = {}
    path = os.path.join(ROOT, "infra", ".env")
    if os.path.exists(path):
        for line in open(path, encoding="utf-8"):
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                key, _, value = line.partition("=")
                values[key.strip()] = value.strip().strip('"').strip("'")
    for key in ("FORGEJO_BOT_TOKEN", "FORGEJO_WEBHOOK_SECRET", "FORGEJO_URL"):
        if os.environ.get(key):
            values[key] = os.environ[key]
    return values


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


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("repository", help="owner/name of an existing repository")
    parser.add_argument("--dry-run", action="store_true", help="say what would be done and change nothing")
    arguments = parser.parse_args()
    if arguments.repository.count("/") != 1 or not all(arguments.repository.split("/")):
        sys.exit("The repository must be written owner/name.")

    env = read_env()
    token, secret = env.get("FORGEJO_BOT_TOKEN", ""), env.get("FORGEJO_WEBHOOK_SECRET", "")
    if not token or not secret:
        sys.exit("FORGEJO_BOT_TOKEN and FORGEJO_WEBHOOK_SECRET must be set in infra/.env (or the environment).")
    forgejo = Forgejo(env.get("FORGEJO_URL", "http://localhost:3000"), token)
    repo = arguments.repository
    dry = arguments.dry_run
    changed = 0

    status, info = forgejo.call("GET", f"repos/{repo}")
    if status != 200:
        sys.exit(f"Cannot read {repo} ({status}). It must exist already, and the bot must be able to see it.")
    print(f"{repo}: default branch {info['default_branch']}{' (empty repository)' if info.get('empty') else ''}")

    # Labels
    _, existing = forgejo.call("GET", f"repos/{repo}/labels?limit=200")
    have = {label["name"].lower() for label in existing or []}
    for name, colour, meaning in LABELS:
        if name in have:
            print(f"  label {name}: already there")
            continue
        changed += 1
        if dry:
            print(f"  label {name}: would create ({meaning})")
            continue
        status, _ = forgejo.call("POST", f"repos/{repo}/labels", {"name": name, "color": colour, "description": meaning})
        print(f"  label {name}: {'created' if status == 201 else f'FAILED ({status}): the token needs write access to the repository'}")

    # Webhook
    _, hooks = forgejo.call("GET", f"repos/{repo}/hooks")
    mine = [hook for hook in hooks or [] if hook.get("config", {}).get("url") == HOOK_URL]
    wanted = {"type": "forgejo", "active": True, "events": EVENTS,
              "config": {"url": HOOK_URL, "content_type": "json", "secret": secret}}
    if not mine:
        changed += 1
        if dry:
            print(f"  webhook: would create ({HOOK_URL}; events {', '.join(EVENTS)})")
        else:
            status, _ = forgejo.call("POST", f"repos/{repo}/hooks", wanted)
            print(f"  webhook: {'created' if status == 201 else f'FAILED ({status})'}")
    else:
        hook = mine[0]
        missing = [event for event in EVENTS if event not in hook.get("events", [])]
        if missing or not hook.get("active", False):
            changed += 1
            if dry:
                print(f"  webhook: would update (missing events: {', '.join(missing) or 'none'}; active: {hook.get('active')})")
            else:
                status, _ = forgejo.call("PATCH", f"repos/{repo}/hooks/{hook['id']}", {"active": True, "events": sorted(set(hook.get('events', [])) | set(EVENTS))})
                print(f"  webhook: {'updated' if status == 200 else f'FAILED ({status})'} (events added: {', '.join(missing) or 'none'})")
        else:
            print("  webhook: already right")

    # .factory/project.json
    branch = info["default_branch"]
    status, file = forgejo.call("GET", f"repos/{repo}/contents/.factory/project.json?ref={branch}")
    if status != 200:
        print(f"  .factory/project.json: MISSING on {branch}. Add one (docs/project-setup.md#4-add-factoryprojectjson, or copy a template from templates/); without it every run stops with a note.")
    else:
        import base64
        try:
            settings = json.loads(base64.b64decode(file["content"]))
            if not isinstance(settings, dict):
                raise ValueError("it is not a JSON object")
            keys = ", ".join(sorted(settings)) or "(empty)"
            print(f"  .factory/project.json: found, keys: {keys}")
            if not settings.get("gates"):
                print("    note: no `gates` (the project's own checks). The factory still works, but nothing checks a change except the agents.")
        except ValueError as error:
            print(f"  .factory/project.json: NOT USABLE ({error}). Fix it on {branch}.")

    print(("Would change" if dry else "Changed") + f" {changed} thing(s)." if changed else "Nothing to change: the repository is ready.")


if __name__ == "__main__":
    main()
