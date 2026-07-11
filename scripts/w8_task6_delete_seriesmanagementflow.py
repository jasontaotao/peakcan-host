"""Delete Flow A (SeriesManagement) from TraceChartViewModel.cs (Task 6)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 64-135: AddSeries + RemoveSeries + blank lines + RecomputeHeights + Compute.
# Expected LoC at Task 6 start: 143 (post-Task-5).
DELETIONS = [(64, 135, "AddSeries + RemoveSeries + RecomputeHeights + Compute")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 143, f"Expected 143 LoC at Task 6 start (post-Task-5), got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class TraceChartViewModel" in text

marker = "    // === Flow A methods moved to TraceChartViewModel/SeriesManagementFlow.cs (W8 Task 6) ===\n"
for i, ln in enumerate(lines):
    if "Flow F methods moved to TraceChartViewModel/ViewportFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow A marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")