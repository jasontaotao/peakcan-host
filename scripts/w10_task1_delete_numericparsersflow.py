"""Delete Flow A (NumericParsers + helpers) from DbcParser.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Dbc/DbcParser.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 3 non-contiguous ranges in 759-LoC file:
# (1) Current + Consume helpers (lines 96-97) — top
# (2) Peek helper (lines 625-633) — middle
# (3) 5 numeric parsers + Expect + SkipUntilSemicolon + IsTopLevelBlockStart (lines 635-757) — middle-bottom
DELETIONS = [
    (96, 97, "Current + Consume helpers"),
    (625, 633, "Peek helper"),
    (635, 757, "5 numeric parsers + Expect + SkipUntilSemicolon + IsTopLevelBlockStart"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 759, f"Expected 759 LoC at Task 1 start, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.Core.Dbc;" in text
assert "public static class DbcParser" in text
assert "private sealed class ParserState" in text
# Public API preserved
assert "public static Result<DbcDocument> Parse(string text, CancellationToken ct = default)" in text
# Nested ParserState fields stay
assert "_pendingMessages" in text
assert "_maxMessageCount" in text

marker = "    // === Flow A methods moved to DbcParser/NumericParsersFlow.cs (W10 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow A marker inserted before line {i + 1} (ParserState closing brace)")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")