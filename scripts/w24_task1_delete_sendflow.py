#!/usr/bin/env python3
"""W24 Task 1 deletion script -- extract Flow A (SendFlow) from DbcSendViewModel.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 + W21 T1 + W22 T1 + W23 T1 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 2`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs 384 -> ~360 LoC.
Extract BuildCurrentSignalValues + 2 CanExecute guards (CanStartDbcCyclic + CanStopDbcCyclic)
-> DbcSendViewModel/SendFlow.cs (NEW partial).

Range: lines 334-357 (24 LoC: 2 CanExecute guards + 1 xmldoc + BuildCurrentSignalValues method + blank + closing brace).
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs")
START_LINE = 334  # 1-indexed: CanStartDbcCyclic method start
END_LINE = 357     # 1-indexed: BuildCurrentSignalValues closing brace (1 blank line stays after)

EXPECTED_MAIN_LOC_AFTER = 360  # 384 - 24 + 1 marker line = 361 - 1 (blank line preserved) = 360

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w24-t1] pre-delete line count: {len(lines)}")

deleted = lines[START_LINE - 1:END_LINE]
print(f"[w24-t1] deleting {len(deleted)} lines (range {START_LINE}-{END_LINE})")

remaining = lines[:START_LINE - 1] + ["    // === Flow A methods moved to DbcSendViewModel/SendFlow.cs (W24 Task 1) ===\n"] + lines[END_LINE:]

FILE.write_text("".join(remaining), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w24-t1] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 2:
    print(f"[w24-t1] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w24-t1] OK: actual {actual} within +/-2 of expected {EXPECTED_MAIN_LOC_AFTER}")