"""Delete Flow A methods from TraceViewerViewModel.cs by line range (Task 3).

Line ranges (1-indexed inclusive, validated against the pre-extraction file):
  Block 1 (AddTraceAsync + xmldoc): lines 207-319 (113 lines)
  Block 2 (RemoveTraceAsync + CanAddTrace + SetMaster + 4 log helpers): lines 321-396 (76 lines)
  Block 3 (OnRegistrySourcesChanged + xmldoc): lines 826-887 (62 lines)
  Block 4 (RemoveOrphanChartSeries + xmldoc): lines 889-903 (15 lines)
  Block 5 (OnDbcLoaded): lines 1289-1297 (9 lines)

Strategy: read file as list of lines, slice out ranges, write back.
This PRESERVES the namespace declaration, using imports, and class braces.
"""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs")

lines = MAIN.read_text(encoding="utf-8").splitlines(keepends=True)

# All line ranges to delete (1-indexed, inclusive)
# Each tuple: (start_line, end_line, description) -- line numbers from the
# pre-extraction 1671-line file as of commit e2c0fb4.
DELETIONS = [
    (207, 319, "AddTraceAsync + xmldoc"),
    (321, 396, "RemoveTraceAsync + CanAddTrace + SetMaster + 4 log helpers"),
    (826, 887, "OnRegistrySourcesChanged + xmldoc"),
    (889, 903, "RemoveOrphanChartSeries + xmldoc"),
    (1289, 1297, "OnDbcLoaded"),
]

# Convert to 0-indexed slices
to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

original_count = len(lines)
print(f"Original line count: {original_count}")

# Validate line numbers
max_line = max(e for _, e, _ in DELETIONS)
assert max_line <= original_count, f"Line {max_line} > file length {original_count}"

# Delete from bottom-up so earlier line numbers stay stable
for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

# Sanity: namespace declaration must still be present
text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text, "namespace declaration missing!"
assert "public sealed partial class TraceViewerViewModel" in text, "class declaration missing!"
assert "public TraceViewerViewModel(" in text, "ctor missing!"

# Insert a flow marker at the top of the class body (after the class brace + ctor signature)
marker = "    // === Flow A methods moved to TraceViewerViewModel/SourceFlow.cs (W3 Task 3) ===\n"
# Insert after the closing brace of the ctor + initial rebind block.
# Easier: find the line "OnRegistrySourcesChanged();" pattern (the initial pull call)
# and insert the marker before it. But that's been deleted in this same script.
# Find the class brace — insert right after the ctor block ends.
# Looking at the file: the class body starts ~line 21 and contains the ctor + OnRegistrySourcesChanged() call.
# We can't easily find the original ctor end anymore (OnRegistrySourcesChanged() init call is gone).
# Fallback: insert marker right after the class brace.
class_brace_idx = next(i for i, ln in enumerate(lines) if "public sealed partial class TraceViewerViewModel" in ln)
# Find the opening { of the class
brace_open_idx = class_brace_idx
# Skip to find the { after the class header — assume it's on the same line or next
while "{" not in lines[brace_open_idx]:
    brace_open_idx += 1
# Insert marker right after this line
lines.insert(brace_open_idx + 1, marker)
print(f"Flow A marker inserted after line {brace_open_idx + 1}")

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")