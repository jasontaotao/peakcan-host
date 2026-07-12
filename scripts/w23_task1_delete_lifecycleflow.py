#!/usr/bin/env python3
"""W23 Task 1 deletion script -- extract Flow A (Lifecycle) from CyclicDbcSendService.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 + W21 T1 + W22 T1 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 2`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/Services/CyclicDbcSendService.cs 383 -> ~267 LoC.
Extract Lifecycle cluster (3 properties + 2 ctors + Start + Stop + StopInner)
-> CyclicDbcSendService/Lifecycle.partial.cs (NEW partial).

Range: lines 80-196 (117 LoC: 3 properties + 2 ctors + Start + Stop + StopInner + xmldoc + blanks).
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/Services/CyclicDbcSendService.cs")
START_LINE = 80   # 1-indexed: IsRunning property start
END_LINE = 196     # 1-indexed: StopInner closing brace (1 blank line stays after)

EXPECTED_MAIN_LOC_AFTER = 267  # 383 - 117 + 1 marker line = 267 LoC

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w23-t1] pre-delete line count: {len(lines)}")

deleted = lines[START_LINE - 1:END_LINE]
print(f"[w23-t1] deleting {len(deleted)} lines (range {START_LINE}-{END_LINE})")

remaining = lines[:START_LINE - 1] + ["    // === Flow A methods moved to CyclicDbcSendService/Lifecycle.partial.cs (W23 Task 1) ===\n"] + lines[END_LINE:]

FILE.write_text("".join(remaining), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w23-t1] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 2:
    print(f"[w23-t1] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w23-t1] OK: actual {actual} within +/-2 of expected {EXPECTED_MAIN_LOC_AFTER}")