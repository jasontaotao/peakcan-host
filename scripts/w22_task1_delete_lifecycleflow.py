#!/usr/bin/env python3
"""W22 Task 1 deletion script -- extract Flow A (Lifecycle) from RecordService.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 + W21 T1 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 2`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/Services/RecordService.cs 375 -> ~265 LoC.
Extract 7 lifecycle methods (StartRecording + StopRecording + StopRecordingInner
+ OnFrame + OnError + StopAsync + Dispose) -> RecordService/Lifecycle.partial.cs (NEW partial).

5 ranges:
  112-145 (34 LoC): StartRecording xmldoc + method
  146-151 (6 LoC):  StopRecording xmldoc + method + blank
  152-185 (34 LoC): StopRecordingInner xmldoc + method
  187-206 (20 LoC): OnFrame xmldoc + method
  208-212 (5 LoC):  OnError xmldoc + method
  280-287 (8 LoC):  StopAsync xmldoc + override
  289-293 (5 LoC):  Dispose xmldoc + override
Total: 112 LoC + 1 marker = 113 LoC deletion.
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/Services/RecordService.cs")

# All ranges in (start, end, marker_or_None) format.
# Apply in reverse line order so earlier ranges don't shift.
# Only first range gets the marker; subsequent ranges leave a blank.
RANGES = [
    (289, 293, ""),
    (280, 287, ""),
    (208, 212, ""),
    (187, 206, ""),
    (152, 185, ""),
    (146, 151, ""),
    (112, 145, "    // === Flow A methods moved to RecordService/Lifecycle.partial.cs (W22 Task 1) ===\n"),
]

EXPECTED_MAIN_LOC_AFTER = 265  # 375 - 112 + 1 marker line + 1 (shared blank) = 265

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w22-t1] pre-delete line count: {len(lines)}")

# Apply ranges from highest to lowest so line numbers don't shift mid-process
for start, end, marker in sorted(RANGES, key=lambda r: -r[0]):
    deleted = lines[start - 1:end]
    print(f"[w22-t1] deleting {len(deleted)} lines (range {start}-{end})")
    if marker:
        lines = lines[:start - 1] + [marker] + lines[end:]
    else:
        lines = lines[:start - 1] + lines[end:]

FILE.write_text("".join(lines), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w22-t1] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 2:
    print(f"[w22-t1] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w22-t1] OK: actual {actual} within +/-2 of expected {EXPECTED_MAIN_LOC_AFTER}")