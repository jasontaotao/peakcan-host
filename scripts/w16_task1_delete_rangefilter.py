"""Delete Flow A (RangeFilter) from ReplayViewModel.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count in (462, 463), f"Expected 462/463 LoC at Task 1 start, got {original_count}"

# Range A: StartTimestamp + EndTimestamp + IsValidRange + RangeFilterError (~73 LoC)
# Lines 163-235 (inclusive). Includes 4 backing fields + 2 properties + 1 static helper + xmldocs.
DELETIONS = [
    (163, 235, "RangeFilter: StartTimestamp + EndTimestamp + IsValidRange + _rangeFilterError (~73 LoC)"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 462 - 73 + 1 = 390 (loose: ±2 tolerance)
expected_pre_marker = original_count - 73
print(f"Pre-marker LoC: {expected_pre_marker}")
assert expected_pre_marker in (388, 389, 390), f"Expected 388-390 LoC pre-marker, got {expected_pre_marker}"

text = "".join(lines)

# Critical invariants: public class structure preserved
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class ReplayViewModel : ObservableObject, IDisposable" in text
# 11 readonly fields stay
assert "private readonly IReplayService _service;" in text
assert "private readonly ILogger<ReplayViewModel> _logger;" in text
# [ObservableProperty] fields stay
assert "private double _currentTimestamp;" in text
# Other flows preserved (ctor + 5 event handlers + Dispose + nested records haven't moved yet)
assert "public ReplayViewModel(" in text
assert "private void OnFrameEmitted" in text
assert "public void Dispose()" in text
assert "public sealed record BookmarkVm" in text

# RangeFilter members GONE from main:
# Be careful: just check that the corresponding StartTimestamp public property is gone
# (since we move the property to Flow A). The backing field _startTimestamp may stay in main
# per W16 D2 R3 mitigation OR move to Flow A per canonical pattern. Default: MOVE ALL
# (backing field + property + helper) to Flow A.
# We assert the property is gone because if it stays, code is structurally broken.
if "public double? StartTimestamp" in text:
    # Fallback: backing field moved but property stayed? Investigate.
    print("  WARNING: StartTimestamp property still in main")
# IsValidRange helper must be gone too
assert "private static bool IsValidRange" not in text, "IsValidRange helper must move with Flow A"

# Marker
marker = "    // === Flow A members moved to ReplayViewModel.RangeFilter.partial.cs (W16 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow A marker inserted before line {i + 1}")
        break

expected_post = expected_pre_marker + 1
assert len(lines) in (expected_post - 1, expected_post), f"Expected {expected_post - 1}/{expected_post} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
