#!/usr/bin/env python3
"""Delete OutputFlow range from ScriptViewModel.cs per W38 Task 2.

The OutputFlow responsibility is non-contiguous in main (fields + const sit at L26-34,
methods sit at L130-185; state/ctor/execution interleave between them). Three
non-contiguous blocks are deleted, preserving _flushTimer field in main.
"""
import sys
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Re-greped post-T1 boundaries (commit 3f01dc1). T2 implementer re-verified.
# Block A: buffer fields + comment
# Block B: MaxOutputLines const + doc
# Block C: ClearOutput + OnOutputReceived + FlushOutputBuffer (contiguous methods block)
BLOCKS = [
    (26, 28),   # Block A: buffer fields (3 LoC)
    (33, 34),   # Block B: MaxOutputLines const (2 LoC)
    (130, 185), # Block C: 3 methods + docs (56 LoC)
]

EXPECTED_DELTA = 61
total_deleted = 0
for start, end in BLOCKS:
    block = lines[start - 1:end]
    print(f"W38 T2: deleting {len(block)} lines ({start}..{end})")
    total_deleted += len(block)

# W19 R1 LESSON: 2/3 loose assertion — warn if delta off by ±3
if abs(total_deleted - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {total_deleted}")

# Delete blocks in REVERSE order so earlier line numbers stay valid
remaining = lines
for start, end in reversed(BLOCKS):
    remaining = remaining[:start - 1] + remaining[end:]

path.write_text("".join(remaining), encoding="utf-8")
print(f"W38 T2: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")