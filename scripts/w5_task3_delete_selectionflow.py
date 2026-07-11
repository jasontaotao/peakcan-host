"""Delete Flow B (Selection) from SignalViewModel.cs (Task 3)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SignalViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 4 ranges in the post-Task-2 507-LoC file:
# (1) Dispose + xmldoc (368-386)
# (2) Reset + xmldoc (427-437)
# (3) OnSignalSelectionChanged + HandlePlotCheckboxClick + xmldoc (439-466)
# (4) ApplyEntries + xmldoc (493-504)
#
# Between ranges 1 and 2: IHostedService no-op (388-394), ResolveValueTableName
# (396-408), SetDbcService (410-414), FormatRawHex (416-423), _dbc field (425)
# STAY in main (Flow A helpers + IHostedService entry points).
DELETIONS = [
    (368, 386, "Dispose + xmldoc"),
    (427, 437, "Reset + xmldoc"),
    (439, 466, "OnSignalSelectionChanged + HandlePlotCheckboxClick + xmldoc"),
    (493, 504, "ApplyEntries + xmldoc"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 508, f"Expected 508 LoC after Task 2 (+1 marker), got {original_count}"

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

marker = "    // === Flow B methods moved to SignalViewModel/SelectionFlow.cs (W5 Task 3) ===\n"
for i, ln in enumerate(lines):
    if "Flow C methods moved to SignalViewModel/ChartFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow B marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")