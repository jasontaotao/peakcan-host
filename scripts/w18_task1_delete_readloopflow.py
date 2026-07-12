"""Delete Flow A (ReadLoopFlow: ReadLoopAsync + SafeEmitReadLoopError) from PeakCanChannel.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count in (388, 389, 390), f"Expected 388/389/390 LoC at Task 1 start, got {original_count}"

# Confirmed ranges:
# Range 1: ReadLoopAsync (line 229-303 = 75 LoC)
# Range 2: SafeEmitReadLoopError (line 317-332 = 16 LoC)
# Total 91 LoC + ~13 LoC blank lines = ~104 LoC (with the xmldoc + blank lines between)
# To stay simple, delete one big contiguous block + a smaller non-contiguous range.
DELETIONS = [
    (229, 304, "ReadLoopAsync (75 LoC, single largest method)"),
    (317, 333, "SafeEmitReadLoopError (16 LoC, error-emission coupling)"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

total_deleted = 0
for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    count = end_line - start0
    total_deleted += count
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({count} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 389 - 92 + 1 = 298 (loose tolerance per W13 T1 2/3 + W17 wc-l-splitlines)
expected_pre_marker = original_count - total_deleted
print(f"Pre-marker LoC: {expected_pre_marker}")
assert expected_pre_marker in (296, 297, 298), f"Expected 296-298 LoC pre-marker, got {expected_pre_marker}"

text = "".join(lines)
assert "namespace PeakCan.Host.Infrastructure.Peak;" in text
assert "public sealed partial class PeakCanChannel : ICanChannel" in text
# ReadLoopAsync GONE
assert "private async Task ReadLoopAsync" not in text
# SafeEmitReadLoopError GONE
assert "private void SafeEmitReadLoopError(ReadLoopError err)" not in text
# Main primitives preserved
assert "private readonly ushort _handle;" in text
assert "public async Task<Result<Unit>> ConnectAsync" in text
assert "private V8ScriptEngine" not in text  # noise from V8 -- but checking we didnt accidentally drop things
# Other flows preserved
assert "private void EmitClassic(TPCANMsg m" in text
assert "private static TPCANBaudrate? ResolveClassicCode" in text
# LoggerMessage partials preserved
assert "LogReadLoopException" in text

# Marker
marker = "    // === Flow A methods moved to PeakCanChannel/ReadLoopFlow.cs (W18 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow A marker inserted before line {i + 1}")
        break

expected_post = expected_pre_marker + 1
assert len(lines) in (expected_post - 1, expected_post, expected_post + 1), f"Expected {expected_post - 1}/{expected_post + 1} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
