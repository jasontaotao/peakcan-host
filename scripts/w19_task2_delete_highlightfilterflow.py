#!/usr/bin/env python3
"""W19 Task 2 deletion script — extract Flow B (HighlightFilterFlow) from TraceViewModel.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 1`) + W17 wc-l-splitlines
CONFIRMED lesson (capture `len(lines)` post-delete as actual).

Target: src/PeakCan.Host.App/ViewModels/TraceViewModel.cs 300 -> ~230 LoC.
Extract OnHighlightTextChanged callback (146-150) + ApplyHighlight (152-169) +
GetMessageIdStats (171-189) + FormatHexWithSpaces (191-215) -> TraceViewModel/HighlightFilterFlow.cs.
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/ViewModels/TraceViewModel.cs")
# 1-indexed inclusive: delete OnHighlightTextChanged xmldoc + method (146-150),
# blank (151), ApplyHighlight (152-169), blank (170),
# GetMessageIdStats xmldoc + method (171-189), blank (190),
# FormatHexWithSpaces xmldoc + method (191-215), blank (216) -> 71 LoC + 1 marker = 72.
START_LINE = 146
END_LINE = 216

EXPECTED_MAIN_LOC_AFTER = 230  # 300 - 71 + 1 marker line = 230 LoC

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w19-t2] pre-delete line count: {len(lines)}")

deleted = lines[START_LINE - 1:END_LINE]
print(f"[w19-t2] deleting {len(deleted)} lines (range {START_LINE}-{END_LINE})")

remaining = lines[:START_LINE - 1] + ["    // === Flow B methods moved to TraceViewModel/HighlightFilterFlow.cs (W19 Task 2) ===\n"] + lines[END_LINE:]

FILE.write_text("".join(remaining), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w19-t2] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 1:
    print(f"[w19-t2] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w19-t2] OK: actual {actual} within +/-1 of expected {EXPECTED_MAIN_LOC_AFTER}")