"""Delete Flow B methods from TraceViewerViewModel.cs by line range (Task 4).

Line ranges (1-indexed inclusive, validated against the post-Task-3 1397-line file):
  Block 1 (Play + Pause + Stop + SeekTo + OnScrubberValueChanged + OnLoopChanged + OnSpeedChanged):
    lines 261-337 (77 lines)
"""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs")

lines = MAIN.read_text(encoding="utf-8").splitlines(keepends=True)

# Lines 261-337: Play (with [RelayCommand] on 261) ... OnSpeedChanged (ends 337)
DELETIONS = [
    (261, 337, "Play/Pause/Stop/SeekTo + 3 partial void (Flow B)"),
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

# Sanity
text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class TraceViewerViewModel" in text
assert "public TraceViewerViewModel(" in text

# Insert Flow B marker just after the existing Flow A marker
marker = "    // === Flow B methods moved to TraceViewerViewModel/TransportFlow.cs (W3 Task 4) ===\n"
for i, ln in enumerate(lines):
    if "Flow A methods moved to TraceViewerViewModel/SourceFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow B marker inserted after line {i + 1}")
        break
else:
    # Fallback: insert after class brace
    class_brace_idx = next(i for i, ln in enumerate(lines) if "public sealed partial class TraceViewerViewModel" in ln)
    while "{" not in lines[class_brace_idx]:
        class_brace_idx += 1
    lines.insert(class_brace_idx + 1, marker)
    print(f"Fallback: Flow B marker inserted after class brace at line {class_brace_idx + 1}")

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")