"""Delete Flow E (Transfer: 0x3E/0x31/0x34/0x36/0x37) from UdsClient.cs (Task 5)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Uds/UdsClient.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 289, f"Expected 289 LoC at Task 5 start (post-T4), got {original_count}"

# Confirmed ranges from grep:
# Range 1: TesterPresentAsync (0x3E) = lines 156-171 (16 LoC)
# Range 2: RoutineControlAsync x 2 overloads (0x31) = lines 173-210 (38 LoC)
# Range 3: RequestDownloadAsync (0x34) + TransferDataAsync (0x36) + RequestTransferExitAsync (0x37) = lines 212-273 (62 LoC)
DELETIONS = [
    (156, 171, "TesterPresentAsync (0x3E)"),
    (173, 210, "RoutineControlAsync x 2 overloads (0x31)"),
    (212, 273, "RequestDownloadAsync (0x34) + TransferDataAsync (0x36) + RequestTransferExitAsync (0x37)"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

total_deleted = 0
for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    count = end_line - start0
    total_deleted += count
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({count} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 289 - 116 + 1 = 174
expected_pre_marker = 289 - total_deleted
assert len(lines) == expected_pre_marker, f"Expected {expected_pre_marker} LoC pre-marker, got {len(lines)}"

text = "".join(lines)
assert "public partial class UdsClient : IDisposable" in text
assert text.count("public UdsClient(") == 3

# All UDS service methods GONE from main (this is the last partial):
assert "public virtual async Task TesterPresentAsync" not in text
assert "public virtual async Task<byte[]> RoutineControlAsync" not in text
assert "public async Task<int> RequestDownloadAsync" not in text
assert "public async Task TransferDataAsync" not in text
assert "public async Task RequestTransferExitAsync" not in text

# Marker
marker = "    // === Flow E methods moved to UdsClient/TransferFlow.cs (W12 Task 5) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow E marker inserted before line {i + 1}")
        break

expected_post = expected_pre_marker + 1
assert len(lines) == expected_post, f"Expected {expected_post} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes, {len(lines)} total")
