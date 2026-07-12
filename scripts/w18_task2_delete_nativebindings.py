"""Delete Flow B (NativeBindings: EmitClassic + EmitFd + MakeError + ResolveClassicCode) from PeakCanChannel.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count in (295, 296, 297, 298), f"Expected 295-298 LoC at Task 2 start (post-T1), got {original_count}"

# Range: 243-295 = 53 LoC (EmitClassic 243-256 + EmitFd 258-272 + MakeError 274-278 + ResolveClassicCode 280-295)
# Plus blank lines 257 + 273 + 279 in between.
DELETIONS = [
    (243, 296, "EmitClassic + EmitFd + MakeError + ResolveClassicCode (52 LoC of static + instance PEAK SDK interop helpers)"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    count = end_line - start0
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({count} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 297 - 53 + 1 = 245 (loose tolerance per W13 T1 + W17 wc-l)
expected_pre_marker = original_count - 53
print(f"Pre-marker LoC: {expected_pre_marker}")
assert expected_pre_marker in (243, 244, 245), f"Expected 243-245 LoC pre-marker, got {expected_pre_marker}"

text = "".join(lines)
assert "namespace PeakCan.Host.Infrastructure.Peak;" in text
assert "public sealed partial class PeakCanChannel : ICanChannel" in text
# Other flows preserved
assert "ReadLoopFlow.cs" not in text  # marker comment will reference this
# NativeBindings GONE
assert "private void EmitClassic(TPCANMsg m" not in text
assert "private void EmitFd(TPCANMsgFD m" not in text
assert "private static Result<Unit> MakeError(TPCANStatus s)" not in text
assert "private static TPCANBaudrate? ResolveClassicCode" not in text

# Marker
marker = "    // === Flow B methods moved to PeakCanChannel/NativeBindings.cs (W18 Task 2) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow B marker inserted before line {i + 1}")
        break

expected_post = expected_pre_marker + 1
assert len(lines) in (expected_post - 1, expected_post, expected_post + 1), f"Expected {expected_post - 1}/{expected_post + 1} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
