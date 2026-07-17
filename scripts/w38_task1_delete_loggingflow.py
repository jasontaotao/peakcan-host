#!/usr/bin/env python3
"""Delete LoggingFlow range from ScriptViewModel.cs per W38 Task 1."""
import re
import sys
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Exact range from Task 0 Step 3 re-grep.
START = 196
END = 225

# Convert to 0-indexed
deleted = lines[START - 1:END]
print(f"W38 T1: deleting {len(deleted)} lines ({START}..{END})")

# W19 R1 LESSON: 2/3 loose assertion — warn if delta off by ±3
EXPECTED_DELTA = 30
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W38 T1: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
