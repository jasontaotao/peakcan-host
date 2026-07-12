"""Delete Flow A (PlaybackLifecycle: Play+Pause+Seek+SetSpeed+Stop+PlayedTimestamp) from ReplayTimeline.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Replay/ReplayTimeline.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count in (469, 470), f"Expected 469/470 LoC at Task 1 start, got {original_count}"

# 1 contiguous range in 469-LoC file: Play (143) + Pause (167) + Seek (176) + SetSpeed (192) + Stop (224) + PlayedTimestamp (242).
# Range 143-249 = 107 LoC covering 6 methods + xmldoc comments.
DELETIONS = [
    (143, 249, "Play + Pause + Seek + SetSpeed + Stop + PlayedTimestamp (~107 LoC)"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 469 - 107 + 1 = 363 (loose: ±1 tolerance)
expected_pre_marker = original_count - 107
print(f"Pre-marker LoC: {expected_pre_marker}")
assert expected_pre_marker in (362, 363), f"Expected 362-363 LoC pre-marker, got {expected_pre_marker}"

text = "".join(lines)

# Critical invariants: state preserved
assert "namespace PeakCan.Host.Core.Replay;" in text
assert "internal sealed partial class ReplayTimeline" in text
# All 18 fields stay
assert "private readonly object _lock" in text
assert "private readonly ILogger _logger" in text
assert "private IReadOnlyList<ReplayFrame> _frames" in text
assert "private Timer? _timer" in text
assert "private Exception? _sinkException" in text
# Properties preserved
assert "public double CurrentTimestamp" in text
assert "public bool IsPlaying" in text
assert "public bool Loop" in text
assert "public bool HasStarted" in text
# SetFrames preserved in main
assert "public void SetFrames(IReadOnlyList<ReplayFrame> frames)" in text
# ctor preserved
assert "public ReplayTimeline(" in text
# OnTick preserved (Flow B hasn't moved yet)
assert "private void OnTick(object? state)" in text
# 7 LoggerMessage partials preserved in main
assert "LogInvalidLoopRegion" in text
assert "LogPlayEntry" in text
assert "LogOnTickEntry" in text

# PlaybackLifecycle methods GONE from main:
assert "public void Play()" not in text
assert "public void Pause()" not in text
assert "public void Seek(double timestamp)" not in text
assert "public void SetSpeed(double multiplier)" not in text
assert "public void Stop()" not in text
assert "private double PlayedTimestamp" not in text

# Marker - insert before closing brace of class
marker = "    // === Flow A methods moved to ReplayTimeline/PlaybackLifecycleFlow.cs (W15 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow A marker inserted before line {i + 1} (class closing brace)")
        break

expected_post = expected_pre_marker + 1
assert len(lines) in (expected_post - 1, expected_post), f"Expected {expected_post - 1}/{expected_post} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
