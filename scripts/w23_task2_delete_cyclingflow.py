#!/usr/bin/env python3
"""W23 Task 2 deletion script -- extract Flow B (Cycling) from CyclicDbcSendService.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 + W21 T1 + W22 T1 + W23 T1 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 2`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/Services/CyclicDbcSendService.cs 267 -> ~115 LoC.
Extract OnTimerTick 151 LoC (LARGEST method, stays inline per W23 D5 + W12-W22 D5 sister)
-> CyclicDbcSendService/Cycling.partial.cs (NEW partial).

Range: lines 80-232 (153 LoC: Flow A marker replacement + blank + OnTimerTick 151 LoC + closing brace).
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/Services/CyclicDbcSendService.cs")
START_LINE = 80  # 1-indexed: replace Flow A marker with Flow B marker
END_LINE = 232   # 1-indexed: OnTimerTick closing brace (1 blank line stays after)

EXPECTED_MAIN_LOC_AFTER = 115  # 267 - 153 + 1 marker line = 115 LoC

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w23-t2] pre-delete line count: {len(lines)}")

deleted = lines[START_LINE - 1:END_LINE]
print(f"[w23-t2] deleting {len(deleted)} lines (range {START_LINE}-{END_LINE})")

remaining = lines[:START_LINE - 1] + ["    // === Flow B methods moved to CyclicDbcSendService/Cycling.partial.cs (W23 Task 2) ===\n"] + lines[END_LINE:]

FILE.write_text("".join(remaining), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w23-t2] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 2:
    print(f"[w23-t2] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w23-t2] OK: actual {actual} within +/-2 of expected {EXPECTED_MAIN_LOC_AFTER}")