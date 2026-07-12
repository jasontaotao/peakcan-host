#!/usr/bin/env python3
"""W22 Task 2 deletion script -- extract Flow B (Format) from RecordService.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 + W21 T1 + W22 T1 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 2`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/Services/RecordService.cs 264 -> ~204 LoC.
Extract 4 format helpers (WriteHeader + WriteFooter + WriteFrame + FormatFlags)
-> RecordService/Format.partial.cs (NEW partial).

Range: lines 181-242 (~62 LoC: 3 blank lines + 4 methods + interim blanks).
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/Services/RecordService.cs")
START_LINE = 181  # 1-indexed: 3 blank lines after ExecuteAsync closing brace
END_LINE = 242    # 1-indexed: FormatFlags closing brace (1 blank line stays after)

EXPECTED_MAIN_LOC_AFTER = 204  # 264 - 62 + 1 marker line + 1 (blank line preserved) = 204

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w22-t2] pre-delete line count: {len(lines)}")

deleted = lines[START_LINE - 1:END_LINE]
print(f"[w22-t2] deleting {len(deleted)} lines (range {START_LINE}-{END_LINE})")

remaining = lines[:START_LINE - 1] + ["    // === Flow B methods moved to RecordService/Format.partial.cs (W22 Task 2) ===\n"] + lines[END_LINE:]

FILE.write_text("".join(remaining), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w22-t2] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 2:
    print(f"[w22-t2] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w22-t2] OK: actual {actual} within +/-2 of expected {EXPECTED_MAIN_LOC_AFTER}")