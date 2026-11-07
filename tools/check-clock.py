#!/usr/bin/env python3
"""Checks that this machine's wall clock is steady, because a clock that steps by seconds breaks anything that times out on it.

It samples the wall clock and the monotonic clock for a while and reports every moment they disagree by more than 50 ms. The monotonic clock
cannot step, so a disagreement is the wall clock jumping. Seen on WSL2: the wall clock jumps +9 s and back every few seconds, and test runners
(vitest, among others) report "Test timed out in 5000ms" in tests that take milliseconds.

    python3 tools/check-clock.py [seconds]      (default 15; exit code 1 if the clock stepped)
"""
import sys
import time

seconds = float(sys.argv[1]) if len(sys.argv) > 1 else 15.0
start = time.monotonic()
last_wall, last_mono = time.time(), start
steps = []
while time.monotonic() - start < seconds:
    time.sleep(0.01)
    wall, mono = time.time(), time.monotonic()
    difference = (wall - last_wall) - (mono - last_mono)
    if abs(difference) > 0.05:
        steps.append((round(mono - start, 1), round(difference, 2)))
    last_wall, last_mono = wall, mono

if not steps:
    print(f"The clock is steady (no step over 50 ms in {seconds:.0f} s).")
    sys.exit(0)
print(f"The wall clock stepped {len(steps)} time(s) in {seconds:.0f} s (second into the check, size of the step in seconds):")
for at, size in steps[:12]:
    print(f"  at {at:>5}s: {size:+.2f} s")
print("Restart WSL (`wsl --shutdown` from Windows) and check the time-sync service (`timedatectl`); see docs/operations.md.")
sys.exit(1)
