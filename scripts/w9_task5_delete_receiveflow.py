"""Delete Flow D (Receive) from IsoTpLayer.cs (Task 5)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 3 non-contiguous ranges in post-Task-4 516-LoC file (+4 markers):
# (1) ProcessFrame xmldoc + method (lines 158-188)
# (2) HandleSingleFrame + HandleFirstFrame (lines 364-415)
# (3) HandleConsecutiveFrame + HandleConsecutiveFrameLocked + xmldoc (lines 418-490)
DELETIONS = [
    (158, 188, "ProcessFrame xmldoc + method"),
    (364, 415, "HandleSingleFrame + HandleFirstFrame"),
    (418, 490, "HandleConsecutiveFrame + HandleConsecutiveFrameLocked"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 516, f"Expected 516 LoC at Task 5 start (post-Task-4), got {original_count}"

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

marker = "    // === Flow D methods moved to IsoTpLayer/ReceiveFlow.cs (W9 Task 5) ===\n"
for i, ln in enumerate(lines):
    if "Flow B methods moved to IsoTpLayer/SendFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow D marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")