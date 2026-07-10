"""Delete Flow D methods from TraceViewerViewModel.cs by line range (Task 5).

Line ranges (1-indexed inclusive, validated against the post-Task-4 1320-line file):
  Block 1 (TogglePlot xmldoc → UnplotSignalFromTableRow end): lines 564-921 (358 lines)
"""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs")

lines = MAIN.read_text(encoding="utf-8").splitlines(keepends=True)

DELETIONS = [
    (564, 921, "TogglePlot → UnplotSignalFromTableRow (Flow D)"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")

max_line = max(e for _, e, _ in DELETIONS)
assert max_line <= original_count, f"Line {max_line} > file length {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class TraceViewerViewModel" in text
assert "public TraceViewerViewModel(" in text

# Insert Flow D marker after the Flow B marker
marker = "    // === Flow D methods moved to TraceViewerViewModel/WatchFlow.cs (W3 Task 5) ===\n"
for i, ln in enumerate(lines):
    if "Flow B methods moved to TraceViewerViewModel/TransportFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow D marker inserted after line {i + 1}")
        break
else:
    class_brace_idx = next(i for i, ln in enumerate(lines) if "public sealed partial class TraceViewerViewModel" in ln)
    while "{" not in lines[class_brace_idx]:
        class_brace_idx += 1
    lines.insert(class_brace_idx + 1, marker)
    print(f"Fallback: Flow D marker inserted after class brace at line {class_brace_idx + 1}")

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")