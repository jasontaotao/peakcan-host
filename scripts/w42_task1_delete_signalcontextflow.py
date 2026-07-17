#!/usr/bin/env python3
"""Delete SignalContextFlow range from WatchedSignalRow.cs per W42 Task 1."""
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Exact range from Phase 1 re-grep (T1 Step 1):
#   _signal field L58, _decimalDigits field L63, Signal property L65,
#   _dbc field L88, Dbc property L89, closing } of Dbc setter L101.
START = 50
END = 101

# Convert to 0-indexed
deleted = lines[START - 1:END]
print(f"W42 T1: deleting {len(deleted)} lines ({START}..{END})")

# W19 R1 LESSON: 2/3 loose assertion — warn if delta off by ±3
EXPECTED_DELTA = 52
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W42 T1: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
