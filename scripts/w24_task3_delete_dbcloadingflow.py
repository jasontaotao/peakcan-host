#!/usr/bin/env python3
"""W24 Task 3 deletion script -- extract Flow C (DbcLoadingFlow) from DbcSendViewModel.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 + W21 T1 + W22 T1 + W23 T1 + W24 T1 + W24 T2 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 2`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs 327 -> ~235 LoC.
Extract 4 methods (Poll + OnLoaded + OnSelectedDbcMessageChanged + OnRateLimitRejectedCountChanged) + 4 xmldoc blocks
-> DbcSendViewModel/DbcLoadingFlow.cs (NEW partial).

Range: lines 110-117 (OnRateLimitRejectedCountChanged 8 LoC) + lines 170-251 (Poll + OnLoaded + OnSelectedDbcMessageChanged 82 LoC) = 90 LoC + 1 marker = 91 LoC.

Per W19 confirmation: `partial void` source-gen hook bodies CAN move to per-flow partial. The generated [NotifyPropertyChangedInvocator] source-gen still works because the field stays in main + the [NotifyPropertyChangedFor] on the field references the property name (string lookup, file-agnostic).
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs")

# All ranges in (start, end, marker_or_None) format.
# Apply in reverse line order so earlier ranges don't shift.
RANGES = [
    (170, 251, "    // === Flow C methods moved to DbcSendViewModel/DbcLoadingFlow.cs (W24 Task 3) ===\n"),
    (110, 117, ""),  # marker already inserted from first range
]

EXPECTED_MAIN_LOC_AFTER = 236  # 327 - 90 + 1 marker line - 2 (blank lines preserved) = 236

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w24-t3] pre-delete line count: {len(lines)}")

# Apply ranges from highest to lowest so line numbers don't shift mid-process
for start, end, marker in sorted(RANGES, key=lambda r: -r[0]):
    deleted = lines[start - 1:end]
    print(f"[w24-t3] deleting {len(deleted)} lines (range {start}-{end})")
    if marker:
        lines = lines[:start - 1] + [marker] + lines[end:]
    else:
        lines = lines[:start - 1] + lines[end:]

FILE.write_text("".join(lines), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w24-t3] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 2:
    print(f"[w24-t3] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w24-t3] OK: actual {actual} within +/-2 of expected {EXPECTED_MAIN_LOC_AFTER}")