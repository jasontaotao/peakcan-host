#!/usr/bin/env python3
"""W19 Task 3 deletion script — extract Flow C (ExportFlow) from TraceViewModel.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W19 T2 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 1`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/ViewModels/TraceViewModel.cs 230 -> ~148 LoC.
Extract ExportCsv [RelayCommand] (147-208) + CsvEscape (210-229) -> TraceViewModel/ExportFlow.cs.
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/ViewModels/TraceViewModel.cs")
# 1-indexed inclusive: delete ExportCsv xmldoc (147-153) + [RelayCommand] attr (154)
# + ExportCsv method (155-208) + blank (209) + CsvEscape xmldoc (210-213) + CsvEscape method (214-229) -> 83 LoC + 1 marker = 84.
START_LINE = 147
END_LINE = 229

EXPECTED_MAIN_LOC_AFTER = 148  # 230 - 83 + 1 marker line = 148 LoC

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w19-t3] pre-delete line count: {len(lines)}")

deleted = lines[START_LINE - 1:END_LINE]
print(f"[w19-t3] deleting {len(deleted)} lines (range {START_LINE}-{END_LINE})")

remaining = lines[:START_LINE - 1] + ["    // === Flow C methods moved to TraceViewModel/ExportFlow.cs (W19 Task 3) ===\n"] + lines[END_LINE:]

FILE.write_text("".join(remaining), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w19-t3] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 1:
    print(f"[w19-t3] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w19-t3] OK: actual {actual} within +/-1 of expected {EXPECTED_MAIN_LOC_AFTER}")