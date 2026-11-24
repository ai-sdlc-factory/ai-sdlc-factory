"""Tests of tools/usage-report.py: choosing a repository with or without its host, and the spelled-out --by value. Standard library only:

    python3 -m unittest discover -s tests/tools
"""
import contextlib
import importlib.util
import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "tools", "usage-report.py")
spec = importlib.util.spec_from_file_location("usage_report", SCRIPT)
usage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(usage)


def line(repository, number, run_id, total=1000):
    return json.dumps({"time": "2026-11-10T09:00:00Z", "run_id": run_id, "kind": "feature", "repository": repository, "number": number, "status": "pr_opened",
                       "input": total, "output": 0, "total": total, "steps": 1})


class UsageReportTests(unittest.TestCase):
    def run_report(self, *arguments):
        with tempfile.TemporaryDirectory() as folder:
            log = Path(folder) / "usage.jsonl"
            log.write_text("\n".join([line("github:acme/shop", 1, "a"), line("acme/shop", 2, "b"), line("github:acme/api", 3, "c")]) + "\n")
            output = io.StringIO()
            with mock.patch.object(sys, "argv", ["usage-report.py", "--file", str(log), *arguments]), contextlib.redirect_stdout(output):
                code = usage.main()
        return code, output.getvalue()

    def test_a_name_without_a_host_matches_the_repository_on_any_host(self):
        code, text = self.run_report("--repo", "acme/shop")
        self.assertEqual(0, code)
        self.assertIn("github:acme/shop", text)
        self.assertIn("acme/shop ", text)
        self.assertNotIn("acme/api", text)

    def test_a_name_with_a_host_matches_only_that_host(self):
        _, text = self.run_report("--repo", "github:acme/shop")
        self.assertIn("github:acme/shop", text)
        self.assertNotIn("acme/api", text)
        self.assertEqual(1, text.count("shop"))

    def test_an_unknown_repository_matches_no_runs(self):
        _, text = self.run_report("--repo", "acme/nothing")
        self.assertIn("No runs match.", text)

    def test_by_repository_is_the_same_as_by_repo(self):
        _, spelled_out = self.run_report("--by", "repository")
        _, short = self.run_report("--by", "repo")
        self.assertEqual(short, spelled_out)


if __name__ == "__main__":
    unittest.main()
