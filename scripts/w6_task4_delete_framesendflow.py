"""Delete Flow A (FrameSend) from SendViewModel.cs (Task 4)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 3 ranges in the post-Task-3 353-LoC file (+1 marker from Task 3 = 354):
# (1) OnRateLimitRejectedCountChanged + xmldoc (111-120)
# (2) SendAsync + [RelayCommand] (213-285)
# (3) 5 log helpers (334-347)
# Adjusted for marker lines from Tasks 1+2+3 (+3).

DELETIONS = [
    (111, 120, "OnRateLimitRejectedCountChanged + xmldoc"),
    (213, 285, "SendAsync"),
    (334, 347, "5 log helpers"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 353, f"Expected 353 LoC after Task 3, got {original_count}"

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

marker = "    // === Flow A methods moved to SendViewModel/FrameSendFlow.cs (W6 Task 4) ===\n"
for i, ln in enumerate(lines):
    if "Flow C methods moved to SendViewModel/LibraryFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow A marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")