#!/usr/bin/env python3
"""Delete FormattedTextFlow range from WatchedSignalRow.cs per W42 Task 3."""
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Exact range from Phase 1 re-grep (T3 Step 1):
#   v3.50.5 PATCH xmldoc L60-65, LatestText xmldoc L66-67, LatestText L68-79,
#   BlueText xmldoc L83-84, BlueText L85-96, DeltaText xmldoc L100-101, DeltaText L102-113.
START = 60
END = 113

# Convert to 0-indexed
deleted = lines[START - 1:END]
print(f"W42 T3: deleting {len(deleted)} lines ({START}..{END})")

# W19 R1 LESSON: 2/3 loose assertion — warn if delta off by ±3
EXPECTED_DELTA = 57
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta} (off by {actual_delta - EXPECTED_DELTA})")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W42 T3: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
