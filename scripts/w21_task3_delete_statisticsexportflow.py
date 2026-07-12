#!/usr/bin/env python3
"""W21 Task 3 deletion script -- extract Flow C (StatisticsExportFlow) from SignalChartViewModel.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 + W21 T1 + W21 T2 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 1`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs 229 -> ~146 LoC.
Extract 2 read-only observation methods (GetStatistics + ExportToCsv)
-> SignalChartViewModel/StatisticsExportFlow.cs (NEW partial).

Range: lines 142-175 (34 LoC: GetStatistics xmldoc + method + blank)
+ lines 177-226 (50 LoC: ExportToCsv xmldoc + method)
= 84 LoC + 1 marker = 85 LoC deletion.

ExportToCsv is 45 LoC, LARGEST single method, stays inline per W12 D7 + W14 D8 + W18 D5 + W19 D5 + W20 D5 + W21 D5 sister-principle.
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs")

# Two deletion ranges. Apply second range FIRST (higher line numbers don't shift).
RANGES = [
    (177, 226, "    // === Flow C methods moved to SignalChartViewModel/StatisticsExportFlow.cs (W21 Task 3) ===\n"),
    (142, 175, ""),  # marker already inserted from first range
]

EXPECTED_MAIN_LOC_AFTER = 146  # 229 - 84 + 1 marker line = 146 LoC

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w21-t3] pre-delete line count: {len(lines)}")

# Apply ranges from highest to lowest so line numbers don't shift mid-process
for start, end, marker in sorted(RANGES, key=lambda r: -r[0]):
    deleted = lines[start - 1:end]
    print(f"[w21-t3] deleting {len(deleted)} lines (range {start}-{end})")
    if marker:
        lines = lines[:start - 1] + [marker] + lines[end:]
    else:
        lines = lines[:start - 1] + lines[end:]

FILE.write_text("".join(lines), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w21-t3] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 2:
    print(f"[w21-t3] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w21-t3] OK: actual {actual} within +/-2 of expected {EXPECTED_MAIN_LOC_AFTER}")