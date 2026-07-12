"""Delete Flow A (Transport wire + Rx + Dispose + test seam) from UdsClient.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Uds/UdsClient.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 2 non-contiguous ranges in 704-LoC file:
# (1) SendRequestAsync (lines 152-177) — wire entry point
# (2) Dispose + SendRequestInternalAsync + OnP2TimeoutFired + OnMessageReceived + PublicOnMessageReceivedForTesting (lines 569-695)
DELETIONS = [
    (152, 177, "SendRequestAsync wire entry point"),
    (569, 695, "Dispose + SendRequestInternalAsync + OnP2TimeoutFired + OnMessageReceived + PublicOnMessageReceivedForTesting"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 704, f"Expected 704 LoC at Task 1 start, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")
print(f"Per W8.5 D7 formula: expected main (pre-marker) = 704 - 153 = 551")

text = "".join(lines)

# Critical invariants - public API + state preserved:
assert "namespace PeakCan.Host.Core.Uds;" in text
# Outer class must be partial
assert "public class UdsClient : IDisposable" in text or "public partial class UdsClient : IDisposable" in text

# 3 ctors preserved in main
assert text.count("public UdsClient(") == 3, "All 3 ctors must remain in main"

# Public properties preserved
assert "public UdsSession Session" in text
assert "public UdsSecurity Security" in text

# Private fields preserved (transport reads them via partial-class visibility)
assert "_isoTp" in text
assert "_responseTcs" in text
assert "_responseCts" in text
assert "_requestLock" in text
assert "_pendingRequestSid" in text
assert "OnP2TimeoutFiredForTesting" in text

# All UDS service methods preserved in main (Flow B-G haven't moved yet)
assert "DiagnosticSessionControlAsync" in text  # Flow B
assert "SecurityAccessAsync" in text  # Flow D
assert "TesterPresentAsync" in text  # Flow E

# Transport methods GONE from main:
assert "private async Task<byte[]> SendRequestInternalAsync" not in text
assert "private void OnP2TimeoutFired()" not in text
assert "private void OnMessageReceived(byte[] data)" not in text
assert "internal void PublicOnMessageReceivedForTesting" not in text

# Marker - insert before closing brace of class
marker = "    // === Flow A methods moved to UdsClient/TransportFlow.cs (W12 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow A marker inserted before line {i + 1} (class closing brace)")
        break

assert len(lines) == 552, f"Expected 552 LoC after Task 1 (post-marker), got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
