"""Delete Flow A (Lifecycle) from IsoTpLayer.cs (Task 7 — LAST extraction)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Single range in post-Task-6 216-LoC file (+6 markers):
# Lines 124-183: 2 ctors + Reset + Dispose (xmldoc + bodies).
DELETIONS = [(124, 183, "2 ctors + Reset + Dispose (Flow A Lifecycle, LAST extraction)")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 216, f"Expected 216 LoC at Task 7 start (post-Task-6), got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.Core.Uds.IsoTp;" in text
assert "public sealed partial class IsoTpLayer" in text

marker = "    // === Flow A methods moved to IsoTpLayer/LifecycleFlow.cs (W9 Task 7 — LAST extraction) ===\n"
for i, ln in enumerate(lines):
    if "Flow C methods moved to IsoTpLayer/MultiFrameTransportFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow A marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")