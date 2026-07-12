#!/usr/bin/env python3
"""W20 Task 1 deletion script -- extract Flow A (PlaybackFlow) from TraceViewerViewModel.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 1`) + W17 wc-l-splitlines
CONFIRMED lesson (capture `len(lines)` post-delete as actual).

Target: src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs 686 -> ~583 LoC.
Extract 7 playback/propagation methods (ClearCanIdFilter + 6 propagation helpers)
-> TraceViewerViewModel/PlaybackFlow.cs (NEW partial #7).

Range: lines 267-370 (104 LoC including ClearCanIdFilter comment + [RelayCommand] attribute
+ method + blanks + 6 propagation methods + trailing blank).
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs")
START_LINE = 267  # 1-indexed: comment for ClearCanIdFilter (v3.4.2 PATCH xmldoc)
END_LINE = 370    # 1-indexed: trailing blank after RebindMasterFromRegistry's closing brace

EXPECTED_MAIN_LOC_AFTER = 583  # 686 - 104 + 1 marker line = 583 LoC

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w20-t1] pre-delete line count: {len(lines)}")

deleted = lines[START_LINE - 1:END_LINE]
print(f"[w20-t1] deleting {len(deleted)} lines (range {START_LINE}-{END_LINE})")

remaining = lines[:START_LINE - 1] + ["    // === Flow G methods moved to TraceViewerViewModel/PlaybackFlow.cs (W20 Task 1) ===\n"] + lines[END_LINE:]

FILE.write_text("".join(remaining), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w20-t1] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 1:
    print(f"[w20-t1] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w20-t1] OK: actual {actual} within +/-1 of expected {EXPECTED_MAIN_LOC_AFTER}")