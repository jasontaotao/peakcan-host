#!/usr/bin/env python3
"""Delete ExecutionFlow range from ScriptViewModel.cs per W38 Task 3.

W19 R1 LESSON ENHANCED (T3 iteration): pre-verified boundaries via grep
post-T2 commit (0c476aa). Range L73..L121 contains the contiguous
ExecutionFlow block:
  - L73   /// <summary>Run the current script.</summary>
  - L74   [RelayCommand(CanExecute = nameof(CanRun))]
  - L75   private async Task RunAsync()
  - ...
  - L121  private bool CanStop() => IsRunning;

L72 is the closing `}` of ctor (blank line between ctor and ExecutionFlow).
L122 is blank (kept as separator to Dispose summary).
"""
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Post-T2 re-grep boundaries (W19 R1 LESSON ENHANCED 3rd iteration)
START = 73  # Run the current script doc comment
END = 121   # closing line: CanStop definition

deleted = lines[START - 1:END]
print(f"W38 T3: deleting {len(deleted)} lines (L{START}..L{END})")

# W19 R1 LESSON: 2/3 loose assertion - warn if delta off by +/-3
EXPECTED_DELTA = 40
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 - expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W38 T3: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
