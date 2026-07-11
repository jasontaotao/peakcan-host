"""Delete Flow C (Library) from MultiFrameSendViewModel.cs (Task 3)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 334-468: 4 library methods + 2 helpers + xmldocs (135 lines).
# Expected LoC after Task 2: 487 (+1 marker from Task 1 = 488).
DELETIONS = [(334, 468, "4 library methods + 2 helpers + xmldocs")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 487, f"Expected 487 LoC after Task 2, got {original_count}"

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

marker = "    // === Flow C methods moved to MultiFrameSendViewModel/LibraryFlow.cs (W7 Task 3) ===\n"
for i, ln in enumerate(lines):
    if "Flow D methods moved to MultiFrameSendViewModel/DbcIntegrationFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow C marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")