#!/usr/bin/env python3
"""W19 Task 1 deletion script — extract Flow A (ReceptionFlow) from TraceViewModel.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 1`) + W17 wc-l-splitlines
CONFIRMED lesson (capture `len(lines)` post-delete as actual).

Target: src/PeakCan.Host.App/ViewModels/TraceViewModel.cs 384 -> ~311 LoC.
Extract RegisterForTesting (137-138) + TryCompletePending (147-148) +
AppendBatchAsync (155-215) -> TraceViewModel/ReceptionFlow.cs.
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/ViewModels/TraceViewModel.cs")
# 1-indexed inclusive: delete RegisterForTesting xmldoc (132-136) + method (137-139),
# blank (139), TryCompletePending xmldoc (140-146) + method (147-148),
# blank (149), AppendBatchAsync xmldoc (150-154) + method (155-215),
# blank (216) -> 85 LoC + 1 marker = 86 LoC removed.
START_LINE = 132
END_LINE = 216

EXPECTED_MAIN_LOC_AFTER = 300  # 384 - 85 + 1 marker line = 300 LoC

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Sanity: post-line counts
print(f"[w19-t1] pre-delete line count: {len(lines)}")

# 1-indexed inclusive: delete [START_LINE..END_LINE]
# Convert to 0-indexed slice: lines[START_LINE-1:END_LINE]
deleted = lines[START_LINE - 1:END_LINE]
print(f"[w19-t1] deleting {len(deleted)} lines (range {START_LINE}-{END_LINE})")

remaining = lines[:START_LINE - 1] + ["    // === Flow A methods moved to TraceViewModel/ReceptionFlow.cs (W19 Task 1) ===\n"] + lines[END_LINE:]

FILE.write_text("".join(remaining), encoding="utf-8")

# Verify
post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w19-t1] post-delete line count: {actual}")

# W13 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED
delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 1:
    print(f"[w19-t1] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w19-t1] OK: actual {actual} within +/-1 of expected {EXPECTED_MAIN_LOC_AFTER}")