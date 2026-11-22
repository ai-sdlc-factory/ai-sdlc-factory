"""Tests of tools/github-hook-ips.py against a fake meta endpoint on localhost."""
import contextlib
import importlib.util
import io
import json
import os
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "tools", "github-hook-ips.py")
spec = importlib.util.spec_from_file_location("github_hook_ips", SCRIPT)
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)

RANGES = ["192.30.252.0/22", "140.82.112.0/20", "2a0a:a440::/29"]


def serve(answer):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_GET(self):
            data = json.dumps(answer).encode()
            self.send_response(200 if self.path == "/meta" else 404)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{server.server_address[1]}"


def run(*arguments):
    out = io.StringIO()
    problem = None
    try:
        with contextlib.redirect_stdout(out):
            tool.main(list(arguments))
    except SystemExit as stop:
        problem = str(stop.code)
    return problem, out.getvalue().rstrip("\n")


class HookIpTests(unittest.TestCase):
    def setUp(self):
        self.server, self.url = serve({"hooks": RANGES, "web": ["1.2.3.4/32"]})
        self.addCleanup(self.server.shutdown)

    def test_one_range_per_line_by_default_and_only_the_webhook_ones(self):
        problem, output = run("--url", self.url)

        self.assertIsNone(problem)
        self.assertEqual(RANGES, output.split("\n"))
        self.assertNotIn("1.2.3.4", output)

    def test_a_cloudflare_rule_blocks_the_path_for_every_other_address(self):
        _, output = run("--url", self.url, "--format", "cloudflare")

        self.assertEqual('(starts_with(http.request.uri.path, "/webhooks/") and not ip.src in {192.30.252.0/22 140.82.112.0/20 2a0a:a440::/29})', output)

    def test_the_protected_path_can_be_changed(self):
        _, output = run("--url", self.url, "--format", "cloudflare", "--path", "/hooks/")

        self.assertIn('starts_with(http.request.uri.path, "/hooks/")', output)

    def test_caddy_and_nginx_forms(self):
        _, caddy = run("--url", self.url, "--format", "caddy")
        _, nginx = run("--url", self.url, "--format", "nginx")

        self.assertEqual("192.30.252.0/22 140.82.112.0/20 2a0a:a440::/29", caddy)
        self.assertEqual("allow 192.30.252.0/22;\nallow 140.82.112.0/20;\nallow 2a0a:a440::/29;\ndeny all;", nginx)

    def test_a_meta_answer_without_hooks_stops_with_an_explanation(self):
        server, url = serve({"web": ["1.2.3.4/32"]})
        self.addCleanup(server.shutdown)

        problem, output = run("--url", url)

        self.assertIn("no list of webhook addresses", problem)
        self.assertEqual("", output)

    def test_an_address_that_does_not_answer_stops_with_a_message(self):
        problem, _ = run("--url", "http://127.0.0.1:9")

        self.assertIn("Could not read", problem)


if __name__ == "__main__":
    unittest.main()
