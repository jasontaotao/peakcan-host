#!/usr/bin/env python3
"""W22 Task 3 deletion script -- extract Flow C (Logging) from RecordService.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 + W21 T1 + W22 T1 + W22 T2 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 2`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/Services/RecordService.cs 203 -> ~182 LoC.
Extract 6 [LoggerMessage] partials (LogRecordingStarted + LogRecordingStopped + LogRecordingFailed
+ LogRecordingStopFailed + LogFrameWriteFailed + LogSinkError) -> RecordService/Logging.partial.cs (NEW partial).

Range: lines 181-202 (22 LoC: Flow B marker replacement + 6 [LoggerMessage] attribute+method pairs
+ interim blanks + closing brace). Net deletion: 21 LoC + 1 marker = 22 LoC.

CRITICAL: Logging.partial.cs must declare `partial class RecordService` to satisfy CS8795.
All 6 partials retain `private static partial` modifier per peakcan-host convention
(NO W18 R1 mitigation -- sister of W20 Phase 1 explore of TraceViewerViewModel SessionFlow/SourceFlow).
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/Services/RecordService.cs")
START_LINE = 181  # 1-indexed: replace Flow B marker with Flow C marker
END_LINE = 202    # 1-indexed: last [LoggerMessage] method declaration

EXPECTED_MAIN_LOC_AFTER = 182  # 203 - 21 + 1 marker line = 183 - 1 (closing brace) = 182

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w22-t3] pre-delete line count: {len(lines)}")

deleted = lines[START_LINE - 1:END_LINE]
print(f"[w22-t3] deleting {len(deleted)} lines (range {START_LINE}-{END_LINE})")

remaining = lines[:START_LINE - 1] + ["    // === Flow C methods moved to RecordService/Logging.partial.cs (W22 Task 3) ===\n"] + lines[END_LINE:]

FILE.write_text("".join(remaining), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w22-t3] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 2:
    print(f"[w22-t3] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w22-t3] OK: actual {actual} within +/-2 of expected {EXPECTED_MAIN_LOC_AFTER}")