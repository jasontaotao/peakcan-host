"""Delete Flow D (Filter/search) from SignalViewModel.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SignalViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 563-601: OnSearchTextChanged + ApplyFilter + xmldoc.
# Verified against v3.19.0 HEAD (601 LoC, commit b3b15a7).
DELETIONS = [(563, 601, "OnSearchTextChanged + ApplyFilter + xmldoc")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 602, f"Expected 602 LoC, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text, "namespace declaration missing!"
assert "public sealed partial class SignalViewModel" in text, "class declaration missing!"
assert "public SignalViewModel(" in text, "ctor missing!"

marker = "    // === Flow D methods moved to SignalViewModel/FilterFlow.cs (W5 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow D marker inserted before line {i + 1} (class closing brace)")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")