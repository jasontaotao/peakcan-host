"""Delete Flow D (Security: 0x27 x 2 overloads) from UdsClient.cs (Task 4)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Uds/UdsClient.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 425, f"Expected 425 LoC at Task 4 start (post-T3), got {original_count}"

# Contiguous range from line 155 to 291 covers both SecurityAccessAsync overloads.
DELETIONS = [
    (155, 291, "SecurityAccessAsync x 2 overloads (3-arg + 2-arg)"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

total_deleted = 0
for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    count = end_line - start0
    total_deleted += count
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({count} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 425 - 137 + 1 = 289
expected_pre_marker = 425 - total_deleted
assert len(lines) == expected_pre_marker, f"Expected {expected_pre_marker} LoC pre-marker, got {len(lines)}"

text = "".join(lines)
assert "public partial class UdsClient : IDisposable" in text
assert text.count("public UdsClient(") == 3

# All other flows still in main
assert "TesterPresentAsync" in text  # Flow E
assert "RequestDownloadAsync" in text  # Flow E

# SecurityFlow methods GONE from main:
assert "public virtual async Task<byte[]> SecurityAccessAsync" not in text

# Marker
marker = "    // === Flow D methods moved to UdsClient/SecurityFlow.cs (W12 Task 4) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow D marker inserted before line {i + 1}")
        break

expected_post = expected_pre_marker + 1
assert len(lines) == expected_post, f"Expected {expected_post} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes, {len(lines)} total")
