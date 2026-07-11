"""Delete Flow A (Channel lifecycle) from AppShellViewModel.cs (Task 4)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 3 ranges in the post-Task-3 623-LoC file:
# (1) OnIsFdChanged + OnSelectedChannelChanged partial void + xmldoc (241-276)
# (2) EnumerateChannels + ConnectAsync + DisconnectAsync + OnReadLoopError + LogReadLoopError (379-614)
DELETIONS = [
    (241, 276, "OnIsFdChanged + OnSelectedChannelChanged partial void + xmldoc"),
    (379, 614, "EnumerateChannels + ConnectAsync + DisconnectAsync + OnReadLoopError + LogReadLoopError"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 624, f"Expected 624 LoC after Task 3, got {original_count}"

# Validate ranges
max_line = max(e for _, e, _ in DELETIONS)
assert max_line <= original_count, f"Line {max_line} > file length {original_count}"

# Delete bottom-up
to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class AppShellViewModel" in text
assert "public AppShellViewModel(" in text

marker = "    // === Flow A methods moved to AppShellViewModel/ChannelFlow.cs (W4 Task 4) ===\n"
for i, ln in enumerate(lines):
    if "Flow B methods moved to AppShellViewModel/ViewSwitchFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow A marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")