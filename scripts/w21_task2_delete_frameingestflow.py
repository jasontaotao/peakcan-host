#!/usr/bin/env python3
"""W21 Task 2 deletion script -- extract Flow B (FrameIngestFlow) from SignalChartViewModel.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 + W21 T1 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 1`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs 313 -> ~229 LoC.
Extract 5 frame-ingest + timer methods (AppendSample + DrainBufferForTest + OnRenderTick + EnsureTimer + StopTimer)
-> SignalChartViewModel/FrameIngestFlow.cs (NEW partial).

Range: lines 139-163 (25 LoC: AppendSample xmldoc + method + blank)
+ lines 253-312 (60 LoC: DrainBufferForTest + OnRenderTick + EnsureTimer + StopTimer + blanks)
= 85 LoC + 1 marker = 86 LoC deletion.

Leaves 165-251 = 87 LoC (GetStatistics + ExportToCsv + xmldoc) for T3.
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs")

# Two deletion ranges. Apply second range FIRST (higher line numbers don't shift).
RANGES = [
    (253, 312, "    // === Flow B methods moved to SignalChartViewModel/FrameIngestFlow.cs (W21 Task 2) ===\n"),
    (139, 163, ""),  # marker already inserted from first range
]

EXPECTED_MAIN_LOC_AFTER = 229  # 313 - 85 + 1 marker line = 229 LoC

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w21-t2] pre-delete line count: {len(lines)}")

# Apply ranges from highest to lowest so line numbers don't shift mid-process
for start, end, marker in sorted(RANGES, key=lambda r: -r[0]):
    deleted = lines[start - 1:end]
    print(f"[w21-t2] deleting {len(deleted)} lines (range {start}-{end})")
    if marker:
        lines = lines[:start - 1] + [marker] + lines[end:]
    else:
        lines = lines[:start - 1] + lines[end:]

FILE.write_text("".join(lines), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w21-t2] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 2:
    print(f"[w21-t2] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w21-t2] OK: actual {actual} within +/-2 of expected {EXPECTED_MAIN_LOC_AFTER}")