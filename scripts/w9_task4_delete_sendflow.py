"""Delete Flow B (Send) from IsoTpLayer.cs (Task 4)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 3 non-contiguous ranges in post-Task-3 612-LoC file (+3 markers):
# (1) SendMessageAsync xmldoc + method (lines 157-182)
# (2) SendSingleFrameAsync + SendCanFrameAsync (lines 242-299)
# (3) SendCanFrame (lines 577-589)
DELETIONS = [
    (157, 182, "SendMessageAsync xmldoc + method"),
    (242, 299, "SendSingleFrameAsync + SendCanFrameAsync"),
    (577, 589, "SendCanFrame"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 612, f"Expected 612 LoC at Task 4 start (post-Task-3), got {original_count}"

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

marker = "    // === Flow B methods moved to IsoTpLayer/SendFlow.cs (W9 Task 4) ===\n"
for i, ln in enumerate(lines):
    if "Flow E methods + WatchdogHandle class + _rxWatchdog field moved to IsoTpLayer/WatchdogFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow B marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")