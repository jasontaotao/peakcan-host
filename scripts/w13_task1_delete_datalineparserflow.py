"""Delete Flow A (DataLineParser: TryParseDataLine) from AscParser.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Replay/AscParser.cs")

content = MAIN.read_text(encoding="utf-8")

# wc -l counts only \n-terminated lines. Python's splitlines(keepends=True)
# counts ALL elements including a final un-terminated line. So a file with
# 513 \n-terminated lines + 1 un-terminated final line reports as 514 to
# splitlines but 513 to wc -l. We use wc -l as the canonical count and
# accept either form from splitlines.
lines = content.splitlines(keepends=True)

# 1 contiguous range in 513-LoC file (wc -l reports 513 because last line missing \n;
# splitlines will report 514 with the unterminated-last-line expansion above adjusted to 513):
# (1) TryParseDataLine (lines 273-427) -- 155 LoC, vector-ASC data line parser.
DELETIONS = [
    (273, 427, "TryParseDataLine (vector-ASC data line parser, 155 LoC)"),
]

original_count = len(lines)
# splitlines may report 514 (last line un-terminated) vs wc -l's 513.
print(f"Original line count: {original_count}")
assert original_count in (513, 514), f"Expected 513/514 LoC at Task 1 start, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 513 - 155 + 1 = 359 (post-marker)
# Pre-marker count: splitlines count (514) minus 155 deleted = 359.
expected_pre_marker = len(lines)  # capture post-delete count
# Allow for the file losing last-\n on re-write.
assert expected_pre_marker in (358, 359), f"Expected 358/359 LoC pre-marker (got {expected_pre_marker})"

text = "".join(lines)

# Critical invariants -- public API + state preserved:
assert "namespace PeakCan.Host.Core.Replay;" in text
assert "public static partial class AscParser" in text, "Outer class must stay partial"
# 4 ParseAsync overloads + ParseAsyncWithHeaderAsync stay in main (>= 5 public statics)
assert text.count("public static ") >= 5, "4 ParseAsync + 1 ParseAsyncWithHeaderAsync expected"
# State preserved
assert "private static ILogger _logger" in text
assert "private static partial void LogSkippedLine" in text
assert "private static readonly char[] WhitespaceSeparators" in text
# Flow B methods preserved (haven't moved yet)
assert "private static (List<ReplayFrame> frames, DateTime? origin, bool timestampsAreAbsolute) ParseLines" in text
assert "private static DateTime? TryParseDateHeader(string line)" in text
# Flow C class declaration preserved (CountingStream hasn't moved yet)
assert "private sealed class CountingStream : Stream" in text

# TryParseDataLine GONE from main:
assert "private static bool TryParseDataLine(string line, out ReplayFrame frame, out string reason)" not in text

# Marker - insert before closing brace of class
marker = "    // === Flow A methods moved to AscParser/DataLineParserFlow.cs (W13 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow A marker inserted before line {i + 1} (class closing brace)")
        break

assert len(lines) in (358, 359, 360), f"Expected 358-360 LoC after Task 1, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
