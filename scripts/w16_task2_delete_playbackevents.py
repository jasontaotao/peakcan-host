"""Delete Flow B (PlaybackEvents: 5 event handlers + Dispose) from ReplayViewModel.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count in (390, 391, 392), f"Expected 390/391/392 LoC at Task 2 start (post-T1), got {original_count}"

# Range: lines 238-343 = 106 LoC (5 handlers + Dispose + xmldocs).
DELETIONS = [
    (232, 344, "PlaybackEvents: xmldoc+OnRecentSessionsPropertyChanged + OnFrameEmitted + OnPlaybackEnded + ApplyPlaybackEnded + OnLoopRewound + Dispose + Dispose close brace (~113 LoC)"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 391 - 113 + 1 = 279 (loose: ±2)
expected_pre_marker = original_count - 113
print(f"Pre-marker LoC: {expected_pre_marker}")
assert expected_pre_marker in (277, 278, 279), f"Expected 277-279 LoC pre-marker, got {expected_pre_marker}"

text = "".join(lines)
# Outer partial still preserved
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class ReplayViewModel" in text
# RangeFilter GONE
assert "public double? StartTimestamp" not in text
# PlaybackEvents GONE
assert "private void OnRecentSessionsPropertyChanged" not in text
assert "private void OnFrameEmitted" not in text
assert "private void OnPlaybackEnded" not in text
assert "private void OnLoopRewound" not in text
assert "public void Dispose()" not in text
# IsValidRange helper GONE
assert "private static bool IsValidRange" not in text
# Other flows preserved (ctor + 13 [ObservableProperty] fields + nested records)
assert "public ReplayViewModel(" in text
assert "private double _currentTimestamp;" in text
assert "public sealed record BookmarkVm" in text
assert "public sealed record LoopRegionVm" in text

# Marker
marker = "    // === Flow B members moved to ReplayViewModel.PlaybackEvents.partial.cs (W16 Task 2) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow B marker inserted before line {i + 1}")
        break

expected_post = expected_pre_marker + 1
assert len(lines) in (expected_post - 1, expected_post, expected_post + 1), f"Expected {expected_post - 1}/{expected_post + 1} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
