"""Delete Flow D (ParseMessage) from DbcParser.cs (Task 4)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Dbc/DbcParser.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 98-170: ParseMessage method.
# Expected LoC at Task 4 start: 317 (post-Task-3).
DELETIONS = [(98, 170, "ParseMessage method")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 317, f"Expected 317 LoC at Task 4 start (post-Task-3), got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.Core.Dbc;" in text
assert "public static partial class DbcParser" in text

marker = "    // === Flow D methods moved to DbcParser/ParseMessageFlow.cs (W10 Task 4) ===\n"
for i, ln in enumerate(lines):
    if "Flow C methods moved to DbcParser/ValueTableFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow D marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")