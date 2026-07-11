"""Delete Flow A (FrameIngest) from SignalViewModel.cs (Task 4)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SignalViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 4 ranges in the post-Task-3 438-LoC file:
# (1) DrainInterval comment + state fields + PendingWork struct + DrainCount (122-148)
# (2) OnDrainTickProxy + ApplyFrame (xmldoc + method) (179-289)
# (3) OnDrainTick (xmldoc + method) + DrainPending (xmldoc + method) (291-357)
# (4) Upsert (comment + method) (411-433)
#
# Between ranges 1+2: ctor + IHostedService no-ops + helpers stay in main.
# Between ranges 3+4: Reset + OnSignalSelectionChanged + HandlePlotCheckboxClick
# + ApplyEntries already extracted to SelectionFlow.cs (Task 3).
DELETIONS = [
    (122, 148, "DrainInterval comment + state + PendingWork + DrainCount"),
    (179, 289, "OnDrainTickProxy + ApplyFrame"),
    (291, 357, "OnDrainTick + DrainPending"),
    (411, 433, "Upsert"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 439, f"Expected 439 LoC after Task 3 (+1 marker), got {original_count}"

# Validate ranges
max_line = max(e for _, e, _ in DELETIONS)
assert max_line <= original_count, f"Line {max_line} > file length {original_count}"

# Delete bottom-up
to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class SignalViewModel" in text
assert "public SignalViewModel(" in text

marker = "    // === Flow A methods moved to SignalViewModel/FrameIngestFlow.cs (W5 Task 4) ===\n"
for i, ln in enumerate(lines):
    if "Flow B methods moved to SignalViewModel/SelectionFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow A marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")