"""Delete Flow B (ParseLines + TryParseDateHeader) from AscParser.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Replay/AscParser.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
# splitlines may report 359 or 360 (last line un-terminated).
print(f"Original line count: {original_count}")
assert original_count in (359, 360), f"Expected 359/360 LoC at Task 2 start (post-T1), got {original_count}"

# Confirmed ranges from grep against post-T1 AscParser.cs:
# Range 1: ParseLines (line 173-245, 73 LoC)
# Range 2: TryParseDateHeader (line 247-271, 25 LoC)
# Total 98 LoC over 99 line slots (one blank line at 246 in between -- counted in deletion).
DELETIONS = [
    (173, 245, "ParseLines dispatcher + main parsing loop + 50%-malformed invariant"),
    (247, 271, "TryParseDateHeader 24h+12h format dispatcher"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

total_deleted = 0
for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    count = end_line - start0
    total_deleted += count
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({count} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: pre-marker count - 99 + 1 = ?
expected_pre_marker = original_count - total_deleted
print(f"Pre-marker LoC: {expected_pre_marker}")

text = "".join(lines)
# Outer partial class still partial
assert "public static partial class AscParser" in text
# 4 ParseAsync overloads + ParseAsyncWithHeaderAsync stay in main (>= 5 public statics)
assert text.count("public static ") >= 5
# State preserved
assert "private static ILogger _logger" in text
assert "private static partial void LogSkippedLine" in text
assert "private static readonly char[] WhitespaceSeparators" in text
# CountingStream class declaration still present (Flow C hasn't moved yet)
assert "private sealed class CountingStream : Stream" in text
# TryParseDataLine GONE (Flow A extracted in Task 1)
assert "private static bool TryParseDataLine(string line, out ReplayFrame frame, out string reason)" not in text

# Flow B methods GONE from main:
assert "private static (List<ReplayFrame> frames, DateTime? origin, bool timestampsAreAbsolute) ParseLines" not in text
assert "private static DateTime? TryParseDateHeader(string line)" not in text

# Marker
marker = "    // === Flow B methods moved to AscParser/ParseLinesFlow.cs (W13 Task 2) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow B marker inserted before line {i + 1}")
        break

expected_post = expected_pre_marker + 1
assert len(lines) in (expected_post - 1, expected_post), f"Expected {expected_post - 1}/{expected_post} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes, {len(lines)} total")
