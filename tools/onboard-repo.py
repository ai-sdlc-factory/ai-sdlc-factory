#!/usr/bin/env python3
"""Sets a repository up for the factory on Forgejo or GitHub: the labels and the webhook (steps 2 and 3 of docs/project-setup.md), and checks .factory/project.json.

    python3 tools/onboard-repo.py owner/repo                                      # the first host of FACTORY_HOSTS (Forgejo: needs nothing else)
    python3 tools/onboard-repo.py owner/repo --host github --public-url https://factory.example.org
    python3 tools/onboard-repo.py owner/repo --dry-run                            # say what would be done, change nothing

It needs an existing repository. It reads the host's kind, webhook secret and, on Forgejo, the bot's token from FACTORY_HOSTS in deploy/.env (or the environment), and never prints a secret
or a token. It is safe to run again: a label or a webhook that is already right is left alone, a webhook with the factory's URL but missing events is updated.

Forgejo: the bot's own token creates the labels, but Forgejo lets only a repository administrator manage webhooks, so either make the bot an administrator of the repository or give your own token (an administrator's, scopes write:repository) in the environment variable named by --token-env. The webhook goes to the factory's api over Docker's network (http://api:8000/webhooks/<host name>), or to <--public-url>/webhooks/<host name>.

GitHub: the bot's token must not be used (it has no right to webhooks, and should not get one), so give your own: a fine-grained token with Webhooks, Issues (labels) and Contents (read) permissions on the
repository, or a classic one with repo and admin:repo_hook, in the environment variable named by --token-env (default GITHUB_TOKEN, then GH_TOKEN; failing that the logged-in `gh` is asked).
GitHub cannot reach a private address, so --public-url, the address of the factory's api as seen from the internet, is required. --url is the API address of a GitHub Enterprise Server (https://<host>/api/v3).
"""
import argparse
import base64
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Name, colour, what it does. The first three are the ones the factory cannot start without.
LABELS = [
    ("feature", "0e8a16", "starts the feature workflow"),
    ("bug", "d73a4a", "starts the bug workflow"),
    ("incident", "b60205", "starts the incident workflow"),
    ("plan", "5319e7", "splits a big goal into feature tickets for a person to approve"),
    ("docs", "0075ca", "writes or updates the repository's documentation"),
    ("quick", "fbca04", "quick mode for this ticket"),
    ("full", "c5def5", "the full process for this ticket, even if the project defaults to quick"),
    ("needs-architecture", "d4c5f9", "forces a design review"),
]


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


class Backend:
    """What differs between the hosts. Every method answers in the same shape, so the steps below are written once."""

    events = []
    token_hint = ""
    page_size = 50  # Forgejo caps a page at 50 by default; GitHub allows 100

    def __init__(self, base, token):
        self.base = base.rstrip("/")
        self.token = token

    def call(self, method, path, body=None):
        request = urllib.request.Request(f"{self.base}/{path}", method=method, data=None if body is None else json.dumps(body).encode(), headers=self.headers())
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                text = response.read().decode()
                return response.status, json.loads(text) if text else None
        except urllib.error.HTTPError as error:
            return error.code, None

    def paged(self, path):
        """Every item of a list, following the pages."""
        items, page = [], 1
        while page <= 20:
            status, chunk = self.call("GET", f"{path}{'&' if '?' in path else '?'}{self.page_query(page)}")
            if status != 200 or not isinstance(chunk, list):
                return None if page == 1 else items
            items += chunk
            if len(chunk) < self.page_size:
                break
            page += 1
        return items


class Forgejo(Backend):
    events = ["issues", "issue_label", "issue_comment", "pull_request", "pull_request_comment", "pull_request_review_rejected"]
    token_hint = "the bot's token needs write access to the repository, and Forgejo lets only a repository administrator manage webhooks: run this again with an administrator's token (MY_TOKEN=... --token-env MY_TOKEN), make the bot an administrator of the repository, or add the webhook in the repository's settings"

    def headers(self):
        return {"Authorization": f"token {self.token}", "Content-Type": "application/json"}

    def page_query(self, page):
        return f"limit={self.page_size}&page={page}"

    def api(self, path):
        return f"api/v1/{path}"

    def repository(self, repo):
        status, info = self.call("GET", self.api(f"repos/{repo}"))
        return None if status != 200 else (info["default_branch"], bool(info.get("empty")))

    def labels(self, repo):
        found = self.paged(self.api(f"repos/{repo}/labels"))
        return {label["name"].lower() for label in found or []}

    def create_label(self, repo, name, colour, meaning):
        status, _ = self.call("POST", self.api(f"repos/{repo}/labels"), {"name": name, "color": "#" + colour, "description": meaning})
        return status == 201, status

    def hooks(self, repo):
        found = self.paged(self.api(f"repos/{repo}/hooks")) or []
        return [{"id": hook["id"], "url": hook.get("config", {}).get("url"), "events": hook.get("events", []), "active": hook.get("active", False)} for hook in found]

    @staticmethod
    def request_events(events):
        """The names to send. Forgejo reports the three review events (`pull_request_review_approved`, `_rejected`, `_comment`) but only accepts
        them under the one name `pull_request_review`; the others are dropped without an error, and a review that requests changes would never arrive."""
        return sorted({"pull_request_review" if event.startswith("pull_request_review_") else event for event in events})

    def create_hook(self, repo, url, secret):
        body = {"type": "forgejo", "active": True, "events": self.request_events(self.events), "config": {"url": url, "content_type": "json", "secret": secret}}
        status, _ = self.call("POST", self.api(f"repos/{repo}/hooks"), body)
        return status == 201, status

    def update_hook(self, repo, hook, events):
        status, _ = self.call("PATCH", self.api(f"repos/{repo}/hooks/{hook['id']}"), {"active": True, "events": self.request_events(events)})
        return status == 200, status

    def read_file(self, repo, path, branch):
        status, file = self.call("GET", self.api(f"repos/{repo}/contents/{path}?ref={branch}"))
        # An empty repository (no commit yet) answers 200 with a list, not a file object.
        return base64.b64decode(file["content"]) if status == 200 and isinstance(file, dict) and "content" in file else None

    def default_hook_url(self, host_name, public_url):
        return f"{public_url.rstrip('/')}/webhooks/{host_name}" if public_url else f"http://api:8000/webhooks/{host_name}"


class GitHub(Backend):
    events = ["issues", "issue_comment", "pull_request_review"]
    token_hint = "the token needs the Webhooks (admin:repo_hook), Issues and Contents permissions on the repository"

    def headers(self):
        return {"Authorization": f"Bearer {self.token}", "Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28",
                "User-Agent": "ai-sdlc-factory-onboard", "Content-Type": "application/json"}

    page_size = 100

    def page_query(self, page):
        return f"per_page={self.page_size}&page={page}"

    def repository(self, repo):
        status, info = self.call("GET", f"repos/{repo}")
        return None if status != 200 else (info["default_branch"], info.get("size", 1) == 0)

    def labels(self, repo):
        found = self.paged(f"repos/{repo}/labels")
        return {label["name"].lower() for label in found or []}

    def create_label(self, repo, name, colour, meaning):
        status, _ = self.call("POST", f"repos/{repo}/labels", {"name": name, "color": colour, "description": meaning})
        return status == 201, status

    def hooks(self, repo):
        found = self.paged(f"repos/{repo}/hooks")
        if found is None:
            return None  # no right to see webhooks (GitHub answers 404 then), or none of this repository
        return [{"id": hook["id"], "url": hook.get("config", {}).get("url"), "events": hook.get("events", []), "active": hook.get("active", False)} for hook in found]

    def create_hook(self, repo, url, secret):
        body = {"name": "web", "active": True, "events": self.events, "config": {"url": url, "content_type": "json", "secret": secret, "insecure_ssl": "0"}}
        status, _ = self.call("POST", f"repos/{repo}/hooks", body)
        return status == 201, status

    def update_hook(self, repo, hook, events):
        status, _ = self.call("PATCH", f"repos/{repo}/hooks/{hook['id']}", {"active": True, "events": events})
        return status == 200, status

    def read_file(self, repo, path, branch):
        status, file = self.call("GET", f"repos/{repo}/contents/{path}?ref={branch}")
        return base64.b64decode(file["content"]) if status == 200 and isinstance(file, dict) and "content" in file else None

    def default_hook_url(self, host_name, public_url):
        return f"{public_url.rstrip('/')}/webhooks/{host_name}"


def operator_token(variable):
    """The token of the person running this, for GitHub: the named variable, GITHUB_TOKEN, GH_TOKEN, or the logged-in gh. Never the bot's."""
    for name in [variable] if variable else ["GITHUB_TOKEN", "GH_TOKEN"]:
        if os.environ.get(name, "").strip():
            return os.environ[name].strip()
    try:
        result = subprocess.run(["gh", "auth", "token"], capture_output=True, text=True, timeout=10)
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip()
    except (OSError, subprocess.SubprocessError):
        pass
    return ""


def ensure_labels(backend, repo, dry):
    """Creates the labels the repository lacks. Returns how many were (or would be) created."""
    have = backend.labels(repo)
    created = 0
    for name, colour, meaning in LABELS:
        if name in have:
            print(f"  label {name}: already there")
            continue
        created += 1
        if dry:
            print(f"  label {name}: would create ({meaning})")
            continue
        ok, status = backend.create_label(repo, name, colour, meaning)
        print(f"  label {name}: {'created' if ok else f'FAILED ({status}): {backend.token_hint}'}")
    return created


def ensure_webhook(backend, repo, secret, hook_url, dry):
    """Creates the factory's webhook, or adds the events a webhook with its URL lacks. Returns 1 when something was (or would be) changed, else 0."""
    hooks = backend.hooks(repo)
    if hooks is None:
        print(f"  webhook: cannot list the webhooks ({backend.token_hint})")
        return 0
    mine = [hook for hook in hooks if hook["url"] == hook_url]
    if not mine:
        if dry:
            print(f"  webhook: would create ({hook_url}; events {', '.join(backend.events)})")
        else:
            ok, status = backend.create_hook(repo, hook_url, secret)
            print(f"  webhook: {'created' if ok else f'FAILED ({status}): {backend.token_hint}'}")
        return 1

    hook = mine[0]
    missing = [event for event in backend.events if event not in hook["events"]]
    if not missing and hook["active"]:
        print("  webhook: already right")
        return 0
    if dry:
        print(f"  webhook: would update (missing events: {', '.join(missing) or 'none'}; active: {hook['active']})")
    else:
        ok, status = backend.update_hook(repo, hook, sorted(set(hook["events"]) | set(backend.events)))
        print(f"  webhook: {'updated' if ok else f'FAILED ({status})'} (events added: {', '.join(missing) or 'none'})")
    return 1


CONTROLLER_IMAGE = "ai-sdlc-factory/controller:local"


def controller_check(content):
    """Asks the controller image to read the file with the reader the runs use (`validate-project -`). Returns (usable, what it said), or None when it cannot be asked: no Docker, no
    image (it is never pulled), or an image built before the command existed. The container has no network."""
    name = f"onboard-check-{os.getpid()}"
    command = ["docker", "run", "--rm", "-i", "--pull", "never", "--network", "none", "--name", name, CONTROLLER_IMAGE,
               "dotnet", "AiSdlcFactory.Controller.dll", "validate-project", "-"]
    try:
        run = subprocess.run(command, input=content, capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        try:
            subprocess.run(["docker", "rm", "-f", name], capture_output=True, timeout=30)
        except (OSError, subprocess.TimeoutExpired):
            pass
        return None
    said = run.stdout.strip()
    if run.returncode == 0 and "is usable" in said:
        return True, said
    if run.returncode == 1 and "cannot be used" in said:
        return False, said
    return None


def check_project_json(backend, repo, branch):
    """Says whether .factory/project.json is on the default branch and usable. Changes nothing."""
    content = backend.read_file(repo, ".factory/project.json", branch)
    if content is None:
        print(f"  .factory/project.json: MISSING on {branch}. Add one (docs/project-setup.md#4-add-factoryprojectjson, or copy a template from templates/); without it every run stops with a note.")
        return
    try:
        settings = json.loads(content)
        if not isinstance(settings, dict):
            raise ValueError("it is not a JSON object")
    except ValueError as error:
        print(f"  .factory/project.json: NOT USABLE ({error}). Fix it on {branch}.")
        return
    print(f"  .factory/project.json: found, keys: {', '.join(sorted(settings)) or '(empty)'}")
    checked = controller_check(content)
    if checked is None:
        print("    note: not checked against the factory's own rules (needs Docker and the controller image: `docker compose build controller`), only that it is JSON.")
    elif checked[0]:
        print("    checked with the factory's own reader: usable.")
    else:
        print(f"    NOT USABLE: {checked[1]} Fix it on {branch}.")
    if not settings.get("gates"):
        print("    note: no `gates` (the project's own checks). The factory still works, but nothing checks a change except the agents.")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("repository", help="owner/name of an existing repository")
    parser.add_argument("--host", help="the name of the host in FACTORY_HOSTS (default: the first one)")
    parser.add_argument("--url", help="where this machine reaches the host's API, when it differs from FACTORY_HOSTS (Forgejo: http://localhost:3000 for http://forgejo:3000; GitHub Enterprise Server: https://<host>/api/v3)")
    parser.add_argument("--public-url", help="the factory's api as the git host reaches it, such as https://factory.example.org (the webhook is <this>/webhooks/<host name>). Required on GitHub")
    parser.add_argument("--token-env", help="the environment variable that holds your own token. GitHub: required in practice (default GITHUB_TOKEN, then GH_TOKEN, then `gh auth token`). Forgejo: an administrator's token, when the bot is not an administrator of the repository (it cannot manage webhooks)")
    parser.add_argument("--dry-run", action="store_true", help="say what would be done and change nothing")
    arguments = parser.parse_args(argv)
    if arguments.repository.count("/") != 1 or not all(arguments.repository.split("/")):
        sys.exit("The repository must be written owner/name.")

    hosts = read_hosts()
    if arguments.host:
        candidates = [h for h in hosts if h.get("name") == arguments.host]
        if not candidates:
            sys.exit(f"No host called {arguments.host} in FACTORY_HOSTS.")
    else:
        candidates = hosts
    host = candidates[0]
    kind = host.get("type", "forgejo")
    if kind not in ("forgejo", "github"):
        sys.exit(f"{host.get('name')} is a {kind} host: this tool knows Forgejo and GitHub. Create the labels and the webhook by hand (docs/git-hosts.md).")
    name, secret = host.get("name", ""), host.get("webhook_secret", "")
    if not secret:
        sys.exit(f"Host {name} in FACTORY_HOSTS needs a webhook_secret.")

    if kind == "github":
        if not arguments.public_url:
            sys.exit("GitHub cannot reach a private address: give --public-url, the factory's api as seen from the internet (https://...), so the webhook can point at it.")
        token = operator_token(arguments.token_env)
        if not token:
            sys.exit("GitHub needs your own token (not the bot's): set GITHUB_TOKEN (or name another variable with --token-env), or log in with `gh auth login`. See --help for the permissions.")
        api = arguments.url or ("https://api.github.com" if host.get("url", "").rstrip("/") in ("", "https://github.com") else host["url"].rstrip("/") + "/api/v3")
        backend = GitHub(api, token)
    else:
        token = (os.environ.get(arguments.token_env, "").strip() if arguments.token_env else "") or host.get("token", "")
        if not token:
            sys.exit(f"Host {name} in FACTORY_HOSTS needs a token (or name one in --token-env).")
        url = arguments.url or host.get("url", "")
        if url.rstrip("/") == "http://forgejo:3000":
            url = "http://localhost:3000"  # the factory's own name for the bundled Forgejo is not known to this machine
        backend = Forgejo(url, token)

    hook_url = backend.default_hook_url(name, arguments.public_url)
    repo, dry = arguments.repository, arguments.dry_run
    found = backend.repository(repo)
    if found is None:
        sys.exit(f"Cannot read {repo}. It must exist already, and the token must be able to see it.")
    branch, empty = found
    print(f"{repo} on {name} ({kind}): default branch {branch}{' (empty repository)' if empty else ''}")

    changed = ensure_labels(backend, repo, dry) + ensure_webhook(backend, repo, secret, hook_url, dry)
    check_project_json(backend, repo, branch)

    if not changed:
        print("Nothing to change: the repository is ready.")
    else:
        print(f"{'Would change' if dry else 'Changed'} {changed} thing(s).")


if __name__ == "__main__":
    main()
