"""Delete Flow B (Send) from MultiFrameSendViewModel.cs (Task 4)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 271-330: SendAsync + CanSend + Stop + CanStop + ModeLabel helper.
# Expected LoC after Task 3: 353 (+1 marker from Task 1 = 354, but Tasks 2+3 markers = 356).
# Adjust assertions based on actual file state.
DELETIONS = [
    (271, 332, "SendAsync + CanSend + Stop + CanStop + ModeLabel"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
# After Task 3, file has +1 marker from Task 1 + +1 from Task 2 = +2 from 351 = 353
# But Task 3 also added +1 = 354
assert original_count == 353, f"Expected 353 LoC after Task 3, got {original_count}"

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

marker = "    // === Flow B methods moved to MultiFrameSendViewModel/SendFlow.cs (W7 Task 4) ===\n"
for i, ln in enumerate(lines):
    if "Flow C methods moved to MultiFrameSendViewModel/LibraryFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow B marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")