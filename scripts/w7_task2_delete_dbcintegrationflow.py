"""Delete Flow D (DbcIntegration) from MultiFrameSendViewModel.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 2 ranges in the post-Task-1 506-LoC file (+1 marker from Task 1 = 507):
# (1) OnRateLimitRejectedCountChanged + xmldoc (104-110)
# (2) OnDbcLoaded + xmldoc (202-214)
DELETIONS = [
    (104, 110, "OnRateLimitRejectedCountChanged + xmldoc"),
    (202, 214, "OnDbcLoaded + xmldoc"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 506, f"Expected 506 LoC after Task 1, got {original_count}"

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
assert "public sealed partial class MultiFrameSendViewModel" in text
assert "public MultiFrameSendViewModel(" in text

marker = "    // === Flow D methods moved to MultiFrameSendViewModel/DbcIntegrationFlow.cs (W7 Task 2) ===\n"
for i, ln in enumerate(lines):
    if "Flow E methods moved to MultiFrameSendViewModel/LifecycleFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow D marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")