"""Delete Flow B (ParseDocument) from DbcParser.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Dbc/DbcParser.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 97-241: ParseDocument method.
# Expected LoC at Task 2 start: 626 (post-Task-1).
DELETIONS = [(97, 241, "ParseDocument method")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 626, f"Expected 626 LoC at Task 2 start (post-Task-1), got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.Core.Dbc;" in text
assert "public static partial class DbcParser" in text
assert "private sealed partial class ParserState" in text

marker = "    // === Flow B methods moved to DbcParser/ParseDocumentFlow.cs (W10 Task 2) ===\n"
for i, ln in enumerate(lines):
    if "Flow A methods moved to DbcParser/NumericParsersFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow B marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")