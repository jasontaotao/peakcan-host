"""Delete Flow C (DataIO + DTC: 0x22/0x2E/0x19/0x14) from UdsClient.cs (Task 3)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Uds/UdsClient.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 484, f"Expected 484 LoC at Task 3 start (post-T2), got {original_count}"

# Confirmed ranges from grep:
# Range 1: ReadDataByIdentifierAsync + WriteDataByIdentifierAsync = lines 154-186 (33 LoC)
# Range 2: ReadDtcInformationAsync + ClearDiagnosticInformationAsync = lines 445-471 (27 LoC)
DELETIONS = [
    (154, 186, "ReadDataByIdentifierAsync (0x22) + WriteDataByIdentifierAsync (0x2E)"),
    (445, 471, "ReadDtcInformationAsync (0x19) + ClearDiagnosticInformationAsync (0x14)"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

total_deleted = 0
for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    count = end_line - start0
    total_deleted += count
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({count} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 484 - 60 + 1 = 425
expected_pre_marker = 484 - total_deleted
assert len(lines) == expected_pre_marker, f"Expected {expected_pre_marker} LoC pre-marker, got {len(lines)}"

text = "".join(lines)
assert "public partial class UdsClient : IDisposable" in text
assert text.count("public UdsClient(") == 3

# All other flows still in main
assert "SecurityAccessAsync" in text  # Flow D
assert "TesterPresentAsync" in text  # Flow E
assert "RequestDownloadAsync" in text  # Flow E

# DataIOFlow methods GONE from main:
assert "public virtual async Task<byte[]> ReadDataByIdentifierAsync" not in text
assert "public virtual async Task WriteDataByIdentifierAsync" not in text
assert "public virtual async Task<byte[]> ReadDtcInformationAsync" not in text
assert "public virtual async Task ClearDiagnosticInformationAsync" not in text

# Marker
marker = "    // === Flow C methods moved to UdsClient/DataIOFlow.cs (W12 Task 3) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow C marker inserted before line {i + 1}")
        break

expected_post = expected_pre_marker + 1
assert len(lines) == expected_post, f"Expected {expected_post} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes, {len(lines)} total")
