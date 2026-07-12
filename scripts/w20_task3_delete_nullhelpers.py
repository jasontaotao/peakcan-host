#!/usr/bin/env python3
"""W20 Task 3 deletion script -- extract Flow C (NullAscServices) from TraceViewerViewModel.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 + W20 T2 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 1`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs 363 -> ~334 LoC.
Extract 2 nested null helper classes (NullAscContentHasher + NullAscLocator)
-> top-level Helpers/NullAscServices.cs (NEW standalone helper file, NOT partial).

Range: lines 334-363 (30 LoC including 2 xmldoc blocks + 2 internal sealed classes + blanks).
After T3: TraceViewerViewModel.cs ctor references at lines 184-185 need to add
`using PeakCan.Host.App.Helpers;` or use fully-qualified name `Helpers.NullAscContentHasher.Instance`.
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs")
START_LINE = 334  # 1-indexed: NullAscContentHasher xmldoc start
END_LINE = 363    # 1-indexed: NullAscLocator closing brace

EXPECTED_MAIN_LOC_AFTER = 334  # 363 - 30 + 1 marker line = 334 LoC

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w20-t3] pre-delete line count: {len(lines)}")

deleted = lines[START_LINE - 1:END_LINE]
print(f"[w20-t3] deleting {len(deleted)} lines (range {START_LINE}-{END_LINE})")

remaining = lines[:START_LINE - 1] + ["\n// === Null helper classes moved to Helpers/NullAscServices.cs (W20 Task 3) ===\n"] + lines[END_LINE:]

FILE.write_text("".join(remaining), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w20-t3] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 1:
    print(f"[w20-t3] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w20-t3] OK: actual {actual} within +/-1 of expected {EXPECTED_MAIN_LOC_AFTER}")