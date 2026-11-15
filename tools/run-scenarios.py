#!/usr/bin/env python3
"""Runs the controller's workflow scenarios in several containers at once, so they take a few minutes instead of seven.

    python3 tools/run-scenarios.py            # build the test image, then 4 containers in parallel
    python3 tools/run-scenarios.py -j 6       # 6 containers
    python3 tools/run-scenarios.py --no-build # use the image `t` as it is (rebuild it yourself after every change!)

The scenarios run the real workflows against a Temporal dev server that starts inside each container, with scripted agents. Inside one container they run one after
another, because they share the sandbox task queue; containers do not share anything, so the scenario classes are split between them (balanced by their number of tests).
It prints one line per container and the failing tests, and exits non-zero when anything failed. Needs Docker; the same as `docker run --rm -e TEST_FILTER='Category=Scenario' t`.
"""
import argparse
import concurrent.futures
import os
import re
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TESTS = os.path.join(ROOT, "services", "controller", "tests", "DevFoundry.Controller.Tests")
TEST_ATTRIBUTE = re.compile(r"\[(?:Skippable)?(?:Fact|Theory)\b")


def scenario_classes():
    """(class name, number of tests) of every class with the Scenario trait, anywhere in the test project, including a second or third class in one file."""
    result = []
    for folder, _, files in os.walk(TESTS):
        if os.sep + "bin" in folder or os.sep + "obj" in folder:
            continue
        for name in files:
            if not name.endswith(".cs"):
                continue
            text = open(os.path.join(folder, name), encoding="utf-8").read()
            if 'Trait("Category", "Scenario")' not in text:
                continue
            # A class's tests are the test attributes between its declaration and the next class declaration.
            declarations = list(re.finditer(r"^public (?:sealed |static |abstract )*class (\w+)", text, re.MULTILINE))
            for position, declaration in enumerate(declarations):
                end = declarations[position + 1].start() if position + 1 < len(declarations) else len(text)
                # The trait sits just above the declaration (with other attributes between them at most).
                lead = text[max(0, declaration.start() - 200):declaration.start()]
                if 'Trait("Category", "Scenario")' in lead:
                    result.append((declaration.group(1), max(1, len(TEST_ATTRIBUTE.findall(text[declaration.start():end])))))
    return result


def partition(classes, groups):
    """Greedy balancing: the biggest class goes to the group with the fewest tests so far."""
    bins = [[0, []] for _ in range(groups)]
    for name, count in sorted(classes, key=lambda item: -item[1]):
        smallest = min(bins, key=lambda b: b[0])
        smallest[0] += count
        smallest[1].append(name)
    return [b[1] for b in bins if b[1]]


def run(index, names, image, rest=False):
    if rest:
        # Everything with the Scenario trait that no group names, so a class the discovery missed is still run.
        filter_text = "Category=Scenario&" + "&".join(f"FullyQualifiedName!~.{name}." for name in names)
    else:
        filter_text = "Category=Scenario&(" + "|".join(f"FullyQualifiedName~.{name}." for name in names) + ")"
    started = time.time()
    process = subprocess.run(["docker", "run", "--rm", "-e", f"TEST_FILTER={filter_text}", image], capture_output=True, text=True)
    return index, names, process.returncode, process.stdout + process.stderr, time.time() - started, rest


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("-j", "--jobs", type=int, default=4, help="containers to run at once (default 4; each needs about 1 GB of memory)")
    parser.add_argument("--no-build", action="store_true", help="do not build the test image first")
    parser.add_argument("--image", default="t", help="the test image (default: t)")
    arguments = parser.parse_args()

    if not arguments.no_build:
        print("Building the test image ...", flush=True)
        build = subprocess.run(["docker", "build", "-q", "--target", "test", "-f", "services/controller/Dockerfile", "-t", arguments.image, "."], cwd=ROOT, capture_output=True, text=True)
        if build.returncode != 0:
            sys.exit("The test image did not build:\n" + (build.stdout + build.stderr)[-2000:])

    classes = scenario_classes()
    groups = partition(classes, max(1, arguments.jobs))
    total = sum(count for _, count in classes)
    print(f"{len(classes)} scenario classes, about {total} tests, in {len(groups)} container(s).", flush=True)

    started = time.time()
    failed = False
    with concurrent.futures.ThreadPoolExecutor(max_workers=len(groups) + 1) as pool:
        jobs = [pool.submit(run, i, g, arguments.image) for i, g in enumerate(groups, 1)]
        jobs.append(pool.submit(run, len(groups) + 1, [name for name, _ in classes], arguments.image, True))
        for index, names, code, output, seconds, rest in (f.result() for f in concurrent.futures.as_completed(jobs)):
            summary = next((line.strip() for line in output.splitlines() if re.match(r"\s*(Passed|Failed)!", line)), "no result line (the container failed to run the tests)")
            if rest and "No test matches" in output:
                summary = "nothing else carries the Scenario trait (as expected)"
                code = 0
            print(f"[{index}]{' (the rest)' if rest else ''} {seconds:5.0f}s  {summary}", flush=True)
            if code != 0 or summary.startswith("no result"):
                failed = True
                for line in output.splitlines():
                    if re.match(r"\s+Failed ", line) or "Error Message" in line:
                        print("      " + line.strip())
                if summary.startswith("no result"):
                    print(output[-1500:])
    print(f"Done in {time.time() - started:.0f}s: {'FAILED' if failed else 'all scenarios passed'}.")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
