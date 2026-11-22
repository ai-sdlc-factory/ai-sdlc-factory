#!/usr/bin/env python3
"""Totals of the factory's usage log: tokens used by finished runs, grouped by day, repository, kind of run, status or ticket.

The controller adds one line per finished run that used an agent to <FACTORY_WORKSPACE_ROOT>/.usage/usage.jsonl on the host. This reads it (only the standard
library), counts each run once even if a line was written twice, and prints a table.

  python3 tools/usage-report.py                      by repository, from the root named in deploy/.env
  python3 tools/usage-report.py --by day --since 2026-11-01
  python3 tools/usage-report.py --by kind --repo acme/shop
  python3 tools/usage-report.py --by ticket --file /path/to/usage.jsonl
"""
import argparse
import json
import os
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GROUPS = ("repo", "day", "month", "kind", "status", "ticket")


def default_file() -> Path:
    """The log under FACTORY_WORKSPACE_ROOT: from the environment, else from deploy/.env."""
    root = os.environ.get("FACTORY_WORKSPACE_ROOT")
    env = ROOT / "deploy/.env"
    if not root and env.exists():
        for line in env.read_text().splitlines():
            if line.startswith("FACTORY_WORKSPACE_ROOT="):
                root = line.split("=", 1)[1].strip().strip('"')
    return Path(root or "/var/tmp/ai-sdlc-factory-workspaces") / ".usage/usage.jsonl"


def read(path: Path) -> tuple[list[dict], int]:
    """The runs in the log, each once (the last line wins), and how many lines could not be read."""
    runs: dict[str, dict] = {}
    unreadable = 0
    for number, line in enumerate(path.read_text().splitlines(), 1):
        if not line.strip():
            continue
        try:
            entry = json.loads(line)
            key = entry["run_id"] or f"line-{number}"
            entry["total"], entry["input"], entry["output"]  # the numbers every report needs
            runs[key] = entry
        except (json.JSONDecodeError, KeyError, TypeError):
            unreadable += 1
    return list(runs.values()), unreadable


def group_of(entry: dict, by: str) -> str:
    if by == "repo":
        return entry["repository"]
    if by == "day":
        return entry["time"][:10]
    if by == "month":
        return entry["time"][:7]
    if by == "kind":
        return entry["kind"]
    if by == "status":
        return entry["status"]
    return f'{entry["repository"]}#{entry["number"]} ({entry["kind"]})'


def number(value: int) -> str:
    return f"{value:,}"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--by", choices=GROUPS, default="repo", help="what to total by (default: repo)")
    parser.add_argument("--since", help="only runs on or after this date, YYYY-MM-DD")
    parser.add_argument("--until", help="only runs on or before this date, YYYY-MM-DD")
    parser.add_argument("--repo", help="only this repository, owner/name")
    parser.add_argument("--kind", help="only this kind of run: feature, bug, incident, plan, revision, qa, update")
    parser.add_argument("--file", type=Path, help="the usage log (default: under FACTORY_WORKSPACE_ROOT)")
    args = parser.parse_args()

    path = args.file or default_file()
    if not path.exists():
        print(f"No usage log at {path}. The controller writes it when a run that used an agent finishes.", file=sys.stderr)
        return 1
    runs, unreadable = read(path)
    runs = [
        run for run in runs
        if (not args.since or run["time"][:10] >= args.since) and (not args.until or run["time"][:10] <= args.until)
        and (not args.repo or run["repository"] == args.repo) and (not args.kind or run["kind"] == args.kind)
    ]
    if not runs:
        print("No runs match.")
        return 0

    totals: dict[str, list[int]] = defaultdict(lambda: [0, 0, 0, 0, 0, 0])
    for run in runs:
        row = totals[group_of(run, args.by)]
        row[0] += 1
        row[1] += run["total"]
        row[2] += run["input"]
        row[3] += run["output"]
        row[4] += run.get("cache_read", 0)
        row[5] += run.get("steps", 0)
    cost = sum(run.get("cost", 0) for run in runs)

    width = max(len(args.by), *(len(name) for name in totals), 5)
    header = f'{args.by:<{width}}  {"runs":>5}  {"tokens":>14}  {"input":>14}  {"output":>12}  {"cache read":>14}  {"agent steps":>11}'
    print(header)
    print("-" * len(header))
    for name, row in sorted(totals.items(), key=lambda item: (-item[1][1], item[0]) if args.by not in ("day", "month") else (0, item[0])):
        print(f"{name:<{width}}  {row[0]:>5}  {number(row[1]):>14}  {number(row[2]):>14}  {number(row[3]):>12}  {number(row[4]):>14}  {number(row[5]):>11}")
    print("-" * len(header))
    everything = [sum(row[i] for row in totals.values()) for i in range(6)]
    print(f'{"all":<{width}}  {everything[0]:>5}  {number(everything[1]):>14}  {number(everything[2]):>14}  {number(everything[3]):>12}  {number(everything[4]):>14}  {number(everything[5]):>11}')
    if cost:
        print(f"cost reported by OpenCode: {cost:.4f}")
    if any(run.get("partial") for run in runs):
        print("Some runs are marked partial: an agent's usage could not be read in full, so their totals are lower bounds.")
    if unreadable:
        print(f"{unreadable} line(s) of the log could not be read and were skipped.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
