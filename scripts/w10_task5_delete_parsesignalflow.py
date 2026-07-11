"""Delete Flow E (ParseSignal) from DbcParser.cs (Task 5 — LAST extraction)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Dbc/DbcParser.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 99-236: ParseSignal method (largest method in DbcParser, ~138 LoC).
# Expected LoC at Task 5 start: 245 (post-Task-4).
DELETIONS = [(99, 236, "ParseSignal method (Flow E, LAST extraction, largest method)")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 245, f"Expected 245 LoC at Task 5 start (post-Task-4), got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.Core.Dbc;" in text
assert "public static partial class DbcParser" in text

marker = "    // === Flow E methods moved to DbcParser/ParseSignalFlow.cs (W10 Task 5 — LAST extraction) ===\n"
for i, ln in enumerate(lines):
    if "Flow D methods moved to DbcParser/ParseMessageFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow E marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")