#!/usr/bin/env python3
"""W24 Task 2 deletion script -- extract Flow B (CyclicFlow) from DbcSendViewModel.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 + W21 T1 + W22 T1 + W23 T1 + W24 T1 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 2`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs 361 -> ~324 LoC.
Extract 2 [RelayCommand] method bodies (StartDbcCyclic + StopDbcCyclic) + xmldoc
-> DbcSendViewModel/CyclicFlow.cs (NEW partial).

Range: lines 295-332 (38 LoC: 2 xmldoc blocks + 2 [RelayCommand] attributes + 2 method bodies + blanks).

Per W7 MultiFrameSendViewModel.SisterFlow.cs precedent + W19 confirmation: [RelayCommand]-annotated method bodies CAN move to per-flow partial. The XxxCommand source-gen property still emits into the same class (cross-partial visibility). NotifyCanExecuteChangedFor on backing fields references the generated command by name (string lookup) so cross-partial [NotifyCanExecuteChangedFor] is safe.
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs")
START_LINE = 298  # 1-indexed: StartDbcCyclic xmldoc start
END_LINE = 332     # 1-indexed: StopDbcCyclic closing brace (2 blank lines stays after)

EXPECTED_MAIN_LOC_AFTER = 326  # 361 - 35 + 1 marker line - 1 (blank line preserved) = 326 LoC

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w24-t2] pre-delete line count: {len(lines)}")

deleted = lines[START_LINE - 1:END_LINE]
print(f"[w24-t2] deleting {len(deleted)} lines (range {START_LINE}-{END_LINE})")

remaining = lines[:START_LINE - 1] + ["    // === Flow B methods moved to DbcSendViewModel/CyclicFlow.cs (W24 Task 2) ===\n"] + lines[END_LINE:]

FILE.write_text("".join(remaining), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w24-t2] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 2:
    print(f"[w24-t2] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w24-t2] OK: actual {actual} within +/-2 of expected {EXPECTED_MAIN_LOC_AFTER}")