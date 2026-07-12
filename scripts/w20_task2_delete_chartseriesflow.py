#!/usr/bin/env python3
"""W20 Task 2 deletion script -- extract Flow B (ChartSeriesFlow) from TraceViewerViewModel.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 1`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs 583 -> ~363 LoC.
Extract BuildOneChartSeriesForSource + FormatCanIdHex + PlotSignal
-> TraceViewerViewModel/ChartSeriesFlow.cs (NEW partial #8).
Leave BuildChartSeries no-op stub (lines 286-303, stays in main per W20 D3).

Range: lines 305-525 (221 LoC including BuildOneChartSeriesForSource xmldoc + method
+ FormatCanIdHex xmldoc + method + PlotSignal xmldoc + method + interim blanks).
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs")
START_LINE = 305  # 1-indexed: BuildOneChartSeriesForSource xmldoc start
END_LINE = 525    # 1-indexed: PlotSignal closing brace

EXPECTED_MAIN_LOC_AFTER = 363  # 583 - 221 + 1 marker line = 363 LoC

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w20-t2] pre-delete line count: {len(lines)}")

deleted = lines[START_LINE - 1:END_LINE]
print(f"[w20-t2] deleting {len(deleted)} lines (range {START_LINE}-{END_LINE})")

remaining = lines[:START_LINE - 1] + ["    // === Flow H methods moved to TraceViewerViewModel/ChartSeriesFlow.cs (W20 Task 2) ===\n"] + lines[END_LINE:]

FILE.write_text("".join(remaining), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w20-t2] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 1:
    print(f"[w20-t2] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w20-t2] OK: actual {actual} within +/-1 of expected {EXPECTED_MAIN_LOC_AFTER}")