#!/usr/bin/env python3
"""Delete ExportFlow range from DbcViewModel.cs per W39 Task 3.

W19 R1 LESSON ENHANCED — 3rd iteration. Re-grep confirmed actual
EXPORT_START=105, EXPORT_END=133 (29 LoC). Plan template said 180..208
but T2 deletion shifted line numbers, so we used the re-grep actual range.
"""
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/DbcViewModel.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Re-grep verified post-T2 (commit 80e41fb)
START = 105
END = 133

# Convert to 0-indexed
deleted = lines[START - 1:END]
print(f"W39 T3: deleting {len(deleted)} lines ({START}..{END})")

EXPECTED_DELTA = 29
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")
else:
    print(f"W39 T3: delta {actual_delta} within W19 R1 ±3 LoC tolerance of {EXPECTED_DELTA}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W39 T3: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
