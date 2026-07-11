"""Delete Flow E (AxisSync) from TraceChartViewModel.cs (Task 4)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 137-200: SyncXAxis xmldoc + SyncXAxis + blank + SyncYAxes xmldoc + SyncYAxes.
# Expected LoC at Task 4 start: 288 (post-Task-3).
DELETIONS = [(137, 200, "SyncXAxis + SyncYAxes")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 288, f"Expected 288 LoC at Task 4 start (post-Task-3), got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class TraceChartViewModel" in text

marker = "    // === Flow E methods moved to TraceChartViewModel/AxisSyncFlow.cs (W8 Task 4) ===\n"
for i, ln in enumerate(lines):
    if "Flow B methods + throttling state moved to TraceChartViewModel/PlaybackFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow E marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")