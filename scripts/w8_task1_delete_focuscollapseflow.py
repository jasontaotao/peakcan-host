"""Delete Flow D (FocusCollapse) from TraceChartViewModel.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 203-238: ToggleCollapse + blank line + SetFocus.
# Expected LoC at Task 1 start: 435.
DELETIONS = [(203, 238, "ToggleCollapse + SetFocus")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 435, f"Expected 435 LoC at Task 1 start, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class TraceChartViewModel" in text

marker = "    // === Flow D methods moved to TraceChartViewModel/FocusCollapseFlow.cs (W8 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow D marker inserted before line {i + 1} (class closing brace)")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")