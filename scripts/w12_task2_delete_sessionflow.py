"""Delete Flow B (SessionFlow: 0x10 + 0x11 + S3 keepalive facades) from UdsClient.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Uds/UdsClient.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 552, f"Expected 552 LoC at Task 2 start (post-T1), got {original_count}"

# Confirmed ranges from grep against post-T1 UdsClient.cs:
# Range 1: DiagnosticSessionControlAsync + EcuResetAsync (2 overloads) = lines 153-210 (58 LoC)
# Range 2: StartTesterPresent + StopTesterPresent = lines 531-541 (11 LoC)
DELETIONS = [
    (153, 210, "DiagnosticSessionControlAsync (0x10) + EcuResetAsync x2 (0x11)"),
    (531, 541, "StartTesterPresent + StopTesterPresent S3 keepalive facades"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

total_deleted = 0
for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    count = end_line - start0
    total_deleted += count
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({count} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 552 - 69 + 1 = 484
expected_after = 552 - total_deleted + 1
assert len(lines) == 552 - total_deleted, f"Expected {552 - total_deleted} LoC pre-marker, got {len(lines)}"

text = "".join(lines)
# Outer partial class still partial
assert "public partial class UdsClient : IDisposable" in text
# 3 ctors still in main
assert text.count("public UdsClient(") == 3

# All other flows still in main
assert "ReadDataByIdentifierAsync" in text  # Flow C
assert "WriteDataByIdentifierAsync" in text  # Flow C
assert "SecurityAccessAsync" in text  # Flow D
assert "TesterPresentAsync" in text  # Flow E
assert "RequestDownloadAsync" in text  # Flow E

# SessionFlow methods GONE from main:
assert "public virtual async Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync" not in text
assert "public void StartTesterPresent" not in text
assert "public void StopTesterPresent" not in text

# Marker
marker = "    // === Flow B methods moved to UdsClient/SessionFlow.cs (W12 Task 2) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow B marker inserted before line {i + 1}")
        break

assert len(lines) == expected_after, f"Expected {expected_after} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes, {len(lines)} total")
