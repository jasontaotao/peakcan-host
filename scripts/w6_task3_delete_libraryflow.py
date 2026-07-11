"""Delete Flow C (Library) from SendViewModel.cs (Task 3)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 3 ranges in the post-Task-2 488-LoC file (+1 marker line from Task 2 = 489):
# (1) Library methods (333-426)
# (2) 2 log helpers (443-450)
# (3) OpenMultiFrameSend + _openMultiFrameWindow field (452-485)
# Adjusted for marker line from Task 2 (+1 to all ranges).
# Actual current file (488 + 1 marker = 489 LoC).
DELETIONS = [
    (333, 426, "RefreshLibrary + SaveCurrentToLibrary + LoadFromLibrary + DeleteFromLibrary"),
    (443, 450, "2 log helpers"),
    (452, 485, "OpenMultiFrameSend + _openMultiFrameWindow field"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
# Adjust: 488 + 1 marker = 489 LoC expected
assert original_count == 488, f"Expected 488 LoC after Task 2, got {original_count}"

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
assert "public sealed partial class SendViewModel" in text
assert "public SendViewModel(" in text

marker = "    // === Flow C methods moved to SendViewModel/LibraryFlow.cs (W6 Task 3) ===\n"
for i, ln in enumerate(lines):
    if "Flow B methods moved to SendViewModel/CyclicFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow C marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")