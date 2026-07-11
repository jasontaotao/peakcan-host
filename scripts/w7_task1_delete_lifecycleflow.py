"""Delete Flow E (Lifecycle) from MultiFrameSendViewModel.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 505-513: Dispose (no xmldoc).
# Verified against v3.21.0 HEAD (513 LoC, commit d63e9cb).
DELETIONS = [(505, 513, "Dispose")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 514, f"Expected 514 LoC (513 + plan file trailing newline), got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text, "namespace missing!"
assert "public sealed partial class MultiFrameSendViewModel" in text, "class declaration missing!"
assert "public MultiFrameSendViewModel(" in text, "ctor missing!"

marker = "    // === Flow E methods moved to MultiFrameSendViewModel/LifecycleFlow.cs (W7 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow E marker inserted before line {i + 1} (class closing brace)")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")