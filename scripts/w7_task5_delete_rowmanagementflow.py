"""Delete Flow A (RowManagement) from MultiFrameSendViewModel.cs (Task 5)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 196-267: OnRowsChanged + RefreshProgressMax + OnIterationsChanged + 6 row methods.
# CanEditRows at line 269 stays in main (used by Flow C library commands).
# Expected LoC after Task 4: 292 (+1 marker from Task 1 = 293).
DELETIONS = [(196, 267, "OnRowsChanged + RefreshProgressMax + OnIterationsChanged + 6 row methods")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 292, f"Expected 292 LoC after Task 4, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class MultiFrameSendViewModel" in text
assert "public MultiFrameSendViewModel(" in text

marker = "    // === Flow A methods moved to MultiFrameSendViewModel/RowManagementFlow.cs (W7 Task 5) ===\n"
for i, ln in enumerate(lines):
    if "Flow B methods moved to MultiFrameSendViewModel/SendFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow A marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")