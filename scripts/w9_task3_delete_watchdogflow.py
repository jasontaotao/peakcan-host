"""Delete Flow E (Watchdog) from IsoTpLayer.cs (Task 3)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 3 non-contiguous ranges in post-Task-2 758-LoC file (+2 markers):
# (1) _rxWatchdog field + xmldoc (lines 81-111)
# (2) WatchdogHandle nested class (lines 121-157)
# (3) StartReceiveWatchdog + CancelReceiveWatchdog (lines 569-647)
DELETIONS = [
    (81, 111, "_rxWatchdog field + xmldoc"),
    (121, 157, "WatchdogHandle nested class"),
    (569, 647, "StartReceiveWatchdog + CancelReceiveWatchdog"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 758, f"Expected 758 LoC at Task 3 start (post-Task-2), got {original_count}"

max_line = max(e for _, e, _ in DELETIONS)
assert max_line <= original_count, f"Line {max_line} > file length {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.Core.Uds.IsoTp;" in text
assert "public sealed partial class IsoTpLayer" in text
# _watchdogDisposalDeferredCount stays in main (test-visible)
assert "_watchdogDisposalDeferredCount" in text

marker = "    // === Flow E methods + WatchdogHandle class + _rxWatchdog field moved to IsoTpLayer/WatchdogFlow.cs (W9 Task 3) ===\n"
for i, ln in enumerate(lines):
    if "Flow G methods moved to IsoTpLayer/LoggingFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow E marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")