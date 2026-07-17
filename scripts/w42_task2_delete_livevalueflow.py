#!/usr/bin/env python3
"""Delete LiveValueFlow range from WatchedSignalRow.cs per W42 Task 2."""
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Exact range from Phase 1 re-grep (T2 Step 1):
#   _latestValue xmldoc L57, _latestValue field L60, LatestValue property L61-83,
#   blue-line xmldoc L84-88, _blueLatestValue field L91, BlueLatestValue L92-104,
#   _blueFrameCount field L106, BlueFrameCount L107-111, DeltaValue xmldoc L113-114,
#   DeltaValue L115-118.
#   Closing: L118 (last line of DeltaValue body `;`).
START = 57
END = 118

# Convert to 0-indexed
deleted = lines[START - 1:END]
print(f"W42 T2: deleting {len(deleted)} lines ({START}..{END})")

# W19 R1 LESSON: 2/3 loose assertion — warn if delta off by ±3
EXPECTED_DELTA = 69
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta} (off by {actual_delta - EXPECTED_DELTA})")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W42 T2: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
