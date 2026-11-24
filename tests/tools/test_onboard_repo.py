"""Tests of tools/onboard-repo.py against a fake Forgejo/GitHub API on localhost. Standard library only:

    python3 -m unittest discover -s tests/tools
"""
import base64
import contextlib
import importlib.util
import io
import json
import os
import re
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "tools", "onboard-repo.py")
spec = importlib.util.spec_from_file_location("onboard_repo", SCRIPT)
onboard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(onboard)

WEBHOOK_SECRET = "hook-secret-0123456789"
BOT_TOKEN = "bot-token-should-never-reach-github"
OPERATOR_TOKEN = "operator-token-abcdef"


def forgejo_events(sent):
    """What Forgejo (17.0.1) stores and reports: it only accepts the review events as `pull_request_review`, drops `pull_request_review_rejected`
    and the like, and reports that one name as three. A fake that echoed the request would hide the bug where the review event never arrived."""
    kept = [event for event in sent if not event.startswith("pull_request_review")]
    if "pull_request_review" in sent:
        kept += ["pull_request_review_approved", "pull_request_review_rejected", "pull_request_review_comment"]
    return kept


class Fake:
    """One repository on a fake host, and a log of every request made to it."""

    def __init__(self):
        self.labels = []
        self.hooks = []
        self.project_json = None
        self.requests = []
        self.next_id = 1
        self.page_cap = 50  # like Forgejo's default
        self.forgejo = True  # GitHub tests turn this off: GitHub stores the events as they were sent

        fake = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def answer(self, status, body=None):
                data = b"" if body is None else json.dumps(body).encode()
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def handle_any(self, method):
                parsed = urlparse(self.path)
                path = re.sub(r"^/api/v1", "", parsed.path)
                query = parse_qs(parsed.query)
                length = int(self.headers.get("Content-Length", "0"))
                body = json.loads(self.rfile.read(length)) if length else None
                fake.requests.append((method, path, self.headers.get("Authorization"), body))
                if path == "/repos/acme/shop" and method == "GET":
                    return self.answer(200, {"default_branch": "main", "empty": False, "size": 1})
                if path == "/repos/acme/missing":
                    return self.answer(404)
                if path == "/repos/acme/shop/labels":
                    if method == "GET":
                        per_page = min(int((query.get("limit") or query.get("per_page"))[0]), fake.page_cap)
                        page = int(query["page"][0])
                        return self.answer(200, [{"name": n} for n in fake.labels[(page - 1) * per_page: page * per_page]])
                    fake.labels.append(body["name"])
                    return self.answer(201, {})
                if path == "/repos/acme/shop/hooks":
                    if method == "GET":
                        return self.answer(200, fake.hooks)
                    hook = {"id": fake.next_id, "events": fake.stored(body["events"]), "active": True, "config": {"url": body["config"]["url"]}}
                    fake.next_id += 1
                    fake.hooks.append(hook)
                    return self.answer(201, hook)
                match = re.fullmatch(r"/repos/acme/shop/hooks/(\d+)", path)
                if match and method == "PATCH":
                    hook = next(h for h in fake.hooks if h["id"] == int(match.group(1)))
                    hook["events"], hook["active"] = fake.stored(body["events"]), body["active"]
                    return self.answer(200, hook)
                if path == "/repos/acme/shop/contents/.factory/project.json":
                    if fake.project_json is None:
                        return self.answer(404)
                    return self.answer(200, {"content": base64.encodebytes(fake.project_json.encode()).decode()})
                self.answer(404)

            def do_GET(self):
                self.handle_any("GET")

            def do_POST(self):
                self.handle_any("POST")

            def do_PATCH(self):
                self.handle_any("PATCH")

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def stored(self, sent):
        return forgejo_events(sent) if self.forgejo else sent

    def close(self):
        self.server.shutdown()
        self.server.server_close()

    def writes(self):
        return [r for r in self.requests if r[0] != "GET"]


def run(*arguments, hosts, env=None, controller=None):
    """Runs the tool; returns (exit message or None, everything it printed). `controller` stands in for the controller image's answer about project.json
    (None: it could not be asked), so no test starts Docker."""
    saved = {k: os.environ.get(k) for k in ("FACTORY_HOSTS", "GITHUB_TOKEN", "GH_TOKEN")}
    os.environ["FACTORY_HOSTS"] = json.dumps(hosts)
    os.environ.pop("GITHUB_TOKEN", None)
    os.environ.pop("GH_TOKEN", None)
    os.environ.update(env or {})
    out = io.StringIO()
    exit_message = None
    original_check = onboard.controller_check
    onboard.controller_check = lambda content: controller
    try:
        with contextlib.redirect_stdout(out):
            onboard.main(list(arguments))
    except SystemExit as stop:
        exit_message = str(stop.code)
    finally:
        onboard.controller_check = original_check
        for key, value in saved.items():
            os.environ.pop(key, None)
            if value is not None:
                os.environ[key] = value
    return exit_message, out.getvalue()


class ForgejoTests(unittest.TestCase):
    def setUp(self):
        self.fake = Fake()
        self.addCleanup(self.fake.close)
        self.hosts = [{"name": "forgejo", "type": "forgejo", "url": self.fake.url, "token": "forgejo-bot-token", "webhook_secret": WEBHOOK_SECRET}]

    def test_a_dry_run_changes_nothing_and_says_what_it_would_do(self):
        problem, output = run("acme/shop", "--dry-run", hosts=self.hosts)

        self.assertIsNone(problem)
        self.assertEqual([], self.fake.writes())
        self.assertIn("would create (http://api:8000/webhooks/forgejo", output)
        self.assertIn("label feature: would create", output)
        self.assertIn("Would change 9 thing(s).", output)

    def test_a_project_json_the_factory_cannot_use_is_reported_before_any_ticket(self):
        self.fake.project_json = '{"gates": [{"name": "t"}], "image": "dotnet", "limits": {"confirm_over": 1000}}'
        _, output = run("acme/shop", hosts=self.hosts, controller=(False, ".factory/project.json cannot be used: \"limits.confirm_over\" must be a whole number."))

        self.assertIn("NOT USABLE: .factory/project.json cannot be used", output)
        self.assertIn("limits.confirm_over", output)

    def test_a_project_json_the_factory_can_use_says_so_and_without_the_image_it_says_it_did_not_check(self):
        self.fake.project_json = '{"gates": [{"name": "t"}], "image": "dotnet"}'
        _, checked = run("acme/shop", hosts=self.hosts, controller=(True, ".factory/project.json is usable."))
        _, unchecked = run("acme/shop", hosts=self.hosts, controller=None)

        self.assertIn("checked with the factory's own reader: usable.", checked)
        self.assertNotIn("not checked against", checked)
        self.assertIn("not checked against the factory's own rules", unchecked)

    def test_it_creates_the_labels_and_the_webhook_and_a_second_run_changes_nothing(self):
        self.fake.project_json = '{"gates": [{"name": "t"}], "image": "dotnet"}'
        problem, output = run("acme/shop", hosts=self.hosts)

        self.assertIsNone(problem)
        self.assertEqual(8, len(self.fake.labels))
        hook = self.fake.hooks[0]
        self.assertEqual("http://api:8000/webhooks/forgejo", hook["config"]["url"])
        self.assertEqual(sorted(forgejo_events(onboard.Forgejo.request_events(onboard.Forgejo.events))), sorted(hook["events"]))
        self.assertTrue(all(r[2] == "token forgejo-bot-token" for r in self.fake.requests))
        self.assertIn(".factory/project.json: found, keys: gates, image", output)

        writes_before = len(self.fake.writes())
        _, again = run("acme/shop", hosts=self.hosts)
        self.assertEqual(writes_before, len(self.fake.writes()))
        self.assertIn("Nothing to change", again)

    def test_the_public_url_is_used_for_a_forgejo_outside_the_factorys_network(self):
        run("acme/shop", "--public-url", "https://factory.example.org/", hosts=self.hosts)

        self.assertEqual("https://factory.example.org/webhooks/forgejo", self.fake.hooks[0]["config"]["url"])

    def test_a_webhook_that_lacks_events_is_updated_not_duplicated(self):
        self.fake.hooks.append({"id": 7, "events": ["issues"], "active": True, "config": {"url": "http://api:8000/webhooks/forgejo"}})
        self.fake.labels += [name for name, _, _ in onboard.LABELS]
        _, output = run("acme/shop", hosts=self.hosts)

        self.assertEqual(1, len(self.fake.hooks))
        self.assertEqual(set(forgejo_events(onboard.Forgejo.request_events(onboard.Forgejo.events))), set(self.fake.hooks[0]["events"]))
        self.assertIn("webhook: updated", output)

    def test_labels_beyond_the_first_page_are_found(self):
        self.fake.labels += [f"filler-{i}" for i in range(60)] + [name for name, _, _ in onboard.LABELS]
        _, output = run("acme/shop", "--dry-run", hosts=self.hosts)

        self.assertIn("label feature: already there", output)
        self.assertNotIn("label feature: would create", output)

    def test_a_missing_project_file_is_reported(self):
        _, output = run("acme/shop", "--dry-run", hosts=self.hosts)

        self.assertIn(".factory/project.json: MISSING on main", output)

    def test_a_repository_that_does_not_exist_stops_it(self):
        problem, _ = run("acme/missing", hosts=self.hosts)

        self.assertIn("Cannot read acme/missing", problem)

    def test_neither_a_token_nor_a_secret_is_ever_printed(self):
        _, output = run("acme/shop", hosts=self.hosts)

        self.assertNotIn(WEBHOOK_SECRET, output)
        self.assertNotIn("forgejo-bot-token", output)


class GitHubTests(unittest.TestCase):
    def setUp(self):
        self.fake = Fake()
        self.fake.forgejo = False
        self.addCleanup(self.fake.close)
        self.hosts = [{"name": "github", "type": "github", "url": "https://github.com", "token": BOT_TOKEN, "webhook_secret": WEBHOOK_SECRET}]
        self.args = ["acme/shop", "--url", self.fake.url, "--public-url", "https://factory.example.org"]

    def test_it_creates_the_webhook_and_the_labels_with_the_operators_token_never_the_bots(self):
        problem, output = run(*self.args, hosts=self.hosts, env={"GITHUB_TOKEN": OPERATOR_TOKEN})

        self.assertIsNone(problem)
        self.assertTrue(all(r[2] == f"Bearer {OPERATOR_TOKEN}" for r in self.fake.requests))
        hook_request = next(r for r in self.fake.requests if r[0] == "POST" and r[1].endswith("/hooks"))
        self.assertEqual("web", hook_request[3]["name"])
        self.assertEqual(["issues", "issue_comment", "pull_request_review"], hook_request[3]["events"])
        self.assertEqual("json", hook_request[3]["config"]["content_type"])
        self.assertEqual("https://factory.example.org/webhooks/github", hook_request[3]["config"]["url"])
        label_request = next(r for r in self.fake.requests if r[0] == "POST" and r[1].endswith("/labels"))
        self.assertNotIn("#", label_request[3]["color"])
        for secret in (WEBHOOK_SECRET, BOT_TOKEN, OPERATOR_TOKEN):
            self.assertNotIn(secret, output)

    def test_a_second_run_changes_nothing(self):
        env = {"GITHUB_TOKEN": OPERATOR_TOKEN}
        run(*self.args, hosts=self.hosts, env=env)
        writes_before = len(self.fake.writes())
        _, output = run(*self.args, hosts=self.hosts, env=env)

        self.assertEqual(writes_before, len(self.fake.writes()))
        self.assertIn("Nothing to change", output)

    def test_a_webhook_that_lacks_events_is_updated(self):
        self.fake.hooks.append({"id": 3, "events": ["issues"], "active": True, "config": {"url": "https://factory.example.org/webhooks/github"}})
        _, output = run(*self.args, hosts=self.hosts, env={"GITHUB_TOKEN": OPERATOR_TOKEN})

        self.assertEqual(1, len(self.fake.hooks))
        self.assertEqual({"issues", "issue_comment", "pull_request_review"}, set(self.fake.hooks[0]["events"]))
        self.assertIn("webhook: updated", output)

    def test_without_a_public_url_it_explains_why_one_is_needed(self):
        problem, _ = run("acme/shop", "--url", self.fake.url, hosts=self.hosts, env={"GITHUB_TOKEN": OPERATOR_TOKEN})

        self.assertIn("--public-url", problem)
        self.assertEqual([], self.fake.requests)

    def test_without_a_token_of_the_operator_it_refuses_and_does_not_fall_back_to_the_bots(self):
        original = onboard.subprocess.run
        onboard.subprocess.run = lambda *a, **k: (_ for _ in ()).throw(OSError("no gh here"))
        self.addCleanup(setattr, onboard.subprocess, "run", original)

        problem, _ = run(*self.args, hosts=self.hosts)

        self.assertIn("your own token", problem)
        self.assertEqual([], self.fake.requests)

    def test_another_variable_can_hold_the_token(self):
        problem, _ = run(*self.args, "--token-env", "MY_PAT", hosts=self.hosts, env={"MY_PAT": "pat-xyz"})

        self.assertIsNone(problem)
        self.assertTrue(all(r[2] == "Bearer pat-xyz" for r in self.fake.requests))
        os.environ.pop("MY_PAT", None)

    def test_a_token_that_may_not_see_webhooks_is_told_what_it_lacks(self):
        original = onboard.GitHub.hooks
        onboard.GitHub.hooks = lambda self, repo: None
        self.addCleanup(setattr, onboard.GitHub, "hooks", original)

        _, output = run(*self.args, "--dry-run", hosts=self.hosts, env={"GITHUB_TOKEN": OPERATOR_TOKEN})

        self.assertIn("cannot list the webhooks (the token needs the Webhooks", output)


class HostChoiceTests(unittest.TestCase):
    def test_a_host_of_another_kind_is_refused_with_a_pointer_to_the_manual_steps(self):
        problem, _ = run("acme/shop", hosts=[{"name": "lab", "type": "gitlab", "url": "http://x", "webhook_secret": "s"}])

        self.assertIn("Forgejo and GitHub", problem)

    def test_an_unknown_host_name_is_reported(self):
        problem, _ = run("acme/shop", "--host", "nope", hosts=[{"name": "forgejo", "url": "http://x", "token": "t", "webhook_secret": "s"}])

        self.assertIn("No host called nope", problem)

    def test_the_repository_must_be_owner_slash_name(self):
        problem, _ = run("just-a-name", hosts=[{"name": "forgejo", "url": "http://x", "token": "t", "webhook_secret": "s"}])

        self.assertIn("owner/name", problem)


if __name__ == "__main__":
    unittest.main()
