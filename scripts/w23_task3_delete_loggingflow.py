#!/usr/bin/env python3
"""W23 Task 3 deletion script -- extract Flow C (Logging) from CyclicDbcSendService.cs.

Sister pattern of W13 T1 + W14 T1 + W15 T1 + W16 T1 + W18 T1 + W19 T1 + W20 T1 + W21 T1 + W22 T1 + W23 T1 + W23 T2 deletion scripts.
Uses W13 T1 2/3 loose-assertion (`abs(actual - expected) <= 2`) + W17 wc-l-splitlines
CONFIRMED lesson.

Target: src/PeakCan.Host.App/Services/CyclicDbcSendService.cs 115 -> ~95 LoC.
Extract 7 [LoggerMessage] partials (LogCyclicDbcStarted + LogCyclicDbcStopped + LogCyclicDbcMessageChanged
+ LogCyclicDbcSendFailed + LogCyclicDbcSendThrew + LogCyclicDbcEncodeThrew + LogCyclicDbcProviderThrew)
-> CyclicDbcSendService/Logging.partial.cs (NEW partial).

Range: lines 95-114 (20 LoC: 7 [LoggerMessage] attribute+method pairs + blanks). Main keeps Dispose (line 82-93).

CRITICAL: Logging.partial.cs must declare `partial class CyclicDbcSendService` to satisfy CS8795.
All 7 partials retain `private static partial` modifier per peakcan-host convention.
"""

import sys
from pathlib import Path

FILE = Path("src/PeakCan.Host.App/Services/CyclicDbcSendService.cs")
START_LINE = 95   # 1-indexed: first [LoggerMessage] attribute
END_LINE = 114     # 1-indexed: last [LoggerMessage] method

EXPECTED_MAIN_LOC_AFTER = 95  # 115 - 20 = 95 LoC

text = FILE.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

print(f"[w23-t3] pre-delete line count: {len(lines)}")

deleted = lines[START_LINE - 1:END_LINE]
print(f"[w23-t3] deleting {len(deleted)} lines (range {START_LINE}-{END_LINE})")

remaining = lines[:START_LINE - 1] + lines[END_LINE:]

FILE.write_text("".join(remaining), encoding="utf-8")

post = FILE.read_text(encoding="utf-8").splitlines(keepends=True)
actual = len(post)
print(f"[w23-t3] post-delete line count: {actual}")

delta = abs(actual - EXPECTED_MAIN_LOC_AFTER)
if delta > 2:
    print(f"[w23-t3] FAIL: actual {actual} vs expected {EXPECTED_MAIN_LOC_AFTER} (delta={delta})")
    sys.exit(1)

print(f"[w23-t3] OK: actual {actual} within +/-2 of expected {EXPECTED_MAIN_LOC_AFTER}")