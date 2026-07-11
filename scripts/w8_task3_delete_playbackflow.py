"""Delete Flow B (Playback) from TraceChartViewModel.cs (Task 3)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 87-157: throttling state xmldoc + 5 fields + UpdatePlaybackCursor + SetTotalDuration.
# Expected LoC at Task 3 start: 358 (post-Task-2).
DELETIONS = [(87, 157, "throttling state block + UpdatePlaybackCursor + SetTotalDuration")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 358, f"Expected 358 LoC at Task 3 start (post-Task-2), got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class TraceChartViewModel" in text

marker = "    // === Flow B methods + throttling state moved to TraceChartViewModel/PlaybackFlow.cs (W8 Task 3) ===\n"
for i, ln in enumerate(lines):
    if "Flow C methods moved to TraceChartViewModel/StatisticsFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow B marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")