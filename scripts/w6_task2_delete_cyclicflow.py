"""Delete Flow B (Cyclic) from SendViewModel.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 332-367 in the post-Task-1 523-LoC file (+1 marker line).
# Expected LoC: 524 (523 + 1 marker from Task 1).
DELETIONS = [(332, 367, "StartCyclic + StopCyclic + xmldoc")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 523, f"Expected 523 LoC after Task 1, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class SendViewModel" in text
assert "public SendViewModel(" in text

marker = "    // === Flow B methods moved to SendViewModel/CyclicFlow.cs (W6 Task 2) ===\n"
for i, ln in enumerate(lines):
    if "Flow D methods moved to SendViewModel/LifecycleFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow B marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")