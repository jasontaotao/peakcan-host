#!/usr/bin/env python3
"""Delete SearchFlow range from DbcViewModel.cs per W39 Task 2."""
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/DbcViewModel.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Exact range from Task 2 Step 1 re-grep (post-T1 boundaries)
START = 109
END = 125

deleted = lines[START - 1:END]
print(f"W39 T2: deleting {len(deleted)} lines ({START}..{END})")

EXPECTED_DELTA = 17
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W39 T2: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
