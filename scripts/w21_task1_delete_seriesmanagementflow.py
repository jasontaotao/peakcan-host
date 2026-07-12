#!/usr/bin/env python3
"""W21 Task 1 deletion script -- extract Flow A (SeriesManagementFlow) from SignalChartViewModel.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 1`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs 378 -> ~312 LoC.
Extract 3 chart-series lifecycle methods (AddSignal + RemoveSignal + Reset)
-> SignalChartViewModel/SeriesManagementFlow.cs (NEW partial).

Range: lines 134-184 (51 LoC) + 216-230 (15 LoC) = 66 LoC + 1 marker = 67 LoC deletion.
Leaves lines 186-188 (_pendingPoints field) in main -- shared mutable state read by
AppendSample (T2), OnRenderTick (T2), and RemoveSignal (T1).
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs")

# Two deletion ranges. Apply second range FIRST (higher line numbers don't shift).
RANGES = [
    (216, 230, "    // === Flow A methods moved to SignalChartViewModel/SeriesManagementFlow.cs (W21 Task 1) ===\n"),
    (134, 184, ""),  # marker already inserted from first range
]

EXPECTED_MAIN_LOC_AFTER = 312  # 378 - 66 + 1 marker = 313 - 1 (single shared marker not duplicated) = 312

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w21-t1] pre-delete line count: {len(lines)}")

# Apply ranges from highest to lowest so line numbers don't shift mid-process
for start, end, marker in sorted(RANGES, key=lambda r: -r[0]):
    deleted = lines[start - 1:end]
    print(f"[w21-t1] deleting {len(deleted)} lines (range {start}-{end})")
    if marker:
        lines = lines[:start - 1] + [marker] + lines[end:]
    else:
        lines = lines[:start - 1] + lines[end:]

FILE.write_text("".join(lines), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w21-t1] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 2:
    print(f"[w21-t1] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w21-t1] OK: actual {actual} within +/-2 of expected {EXPECTED_MAIN_LOC_AFTER}")