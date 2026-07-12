"""Delete Flow B (OnTick) from ReplayTimeline.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Replay/ReplayTimeline.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count in (362, 363, 364), f"Expected 362/363/364 LoC at Task 2 start (post-T1), got {original_count}"

# 1 contiguous range: OnTick body lines 144-322 (179 LoC).
DELETIONS = [
    (144, 322, "OnTick (playback tick, single largest method, ~179 LoC)"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 363 - 179 + 1 = 185 (with loose tolerance)
expected_pre_marker = original_count - 179
print(f"Pre-marker LoC: {expected_pre_marker}")
assert expected_pre_marker in (183, 184, 185), f"Expected 183-185 LoC pre-marker, got {expected_pre_marker}"

text = "".join(lines)
assert "internal sealed partial class ReplayTimeline" in text
# State preserved
assert "private readonly object _lock" in text
assert "private Timer? _timer" in text
# PlaybackLifecycle + OnTick GONE
assert "public void Play()" not in text
assert "private void OnTick" not in text

# Marker
marker = "    // === Flow B methods moved to ReplayTimeline/OnTickFlow.cs (W15 Task 2) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow B marker inserted before line {i + 1}")
        break

expected_post = expected_pre_marker + 1
assert len(lines) in (expected_post - 1, expected_post, expected_post + 1), f"Expected {expected_post - 1}/{expected_post + 1} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
