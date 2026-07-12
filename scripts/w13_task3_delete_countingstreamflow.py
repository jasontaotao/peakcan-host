"""Delete Flow C (CountingStream methods) from AscParser.cs (Task 3)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Replay/AscParser.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count in (262, 263), f"Expected 262/263 LoC at Task 3 start (post-T2), got {original_count}"

# Confirmed ranges from grep:
# Range 1: CountingStream methods body = lines 197-259 (ctor + 4 props + 3 overrides + 2 helpers + 3 throw-NotSupported)
# KEEP in main: line 191 (`private sealed class CountingStream : Stream {`)
#               lines 192-196 (nested 3 fields) -- but wait -- the partial class opens here.
#               Better strategy: move EVERYTHING from line 192 to 259 (except the field declarations
#               stay since the partial class declaration in main needs them) -- but the field declarations
#               belong to the COUNTINGSTREAM nested class scope. So:
#               - Main keeps: line 191 (declaration -> partial), lines 192-196 (fields), line 260 (closing brace)
#               - Move to Flow C: lines 197-259 (ctor + all methods = 63 LoC)
DELETIONS = [
    (197, 259, "CountingStream ctor + 4 properties + 3 Read/Flush/Write overrides + 2 helpers + 3 throw-NotSupported"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

total_deleted = 0
for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    count = end_line - start0
    total_deleted += count
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({count} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula:
expected_pre_marker = original_count - total_deleted
print(f"Pre-marker LoC: {expected_pre_marker}")

text = "".join(lines)
# Outer partial class still partial
assert "public static partial class AscParser" in text
# State preserved
assert "private static ILogger _logger" in text
assert "private static partial void LogSkippedLine" in text
# CountingStream class declaration STILL in main (line 191 in original - shifted)
assert "private sealed class CountingStream : Stream" in text
# CountingStream fields STILL in main
assert "_inner" in text
assert "_maxBytes" in text
assert "_count" in text

# Flow A + B markers preserved
assert "Flow A methods moved" in text
assert "Flow B methods moved" in text

# All CountingStream method bodies GONE from main:
assert "public CountingStream(Stream inner, long maxBytes)" not in text
assert "public override bool CanRead => _inner.CanRead;" not in text
assert "public override void Flush() => _inner.Flush();" not in text
assert "public override void Write(" not in text

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes, {len(lines)} total")
