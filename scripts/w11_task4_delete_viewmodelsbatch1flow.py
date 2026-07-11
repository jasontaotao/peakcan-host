"""Delete Flow D (ViewModelsBatch1) from AppHostBuilder.cs (Task 4).

W11 helper extraction pattern (D5):
- Replace ViewModels batch 1 section in Build() body with a call to
  RegisterViewModelsBatch1(builder.Services) helper.
"""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/Composition/AppHostBuilder.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 117-167: ViewModels batch 1 section.
# Expected LoC at Task 4 start: 517 (post-Task-3).
DELETIONS = [(117, 167, "ViewModels batch 1 section (SequenceSend through Replay)")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 517, f"Expected 517 LoC at Task 4 start (post-Task-3), got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Insert helper call + marker BEFORE "// v3.0 MINOR Task 7: Trace Viewer" section
new_helper_call = """        // === Flow D: ViewModels batch 1 extracted to AppHostBuilder/ViewModelsBatch1Flow.cs (W11 Task 4) ===
        RegisterViewModelsBatch1(builder.Services);

"""

insert_pos = None
for i, ln in enumerate(lines):
    if "// v3.0 MINOR Task 7: Trace Viewer" in ln:
        insert_pos = i
        break

assert insert_pos is not None, "Could not find // v3.0 MINOR Task 7 marker"

lines.insert(insert_pos, new_helper_call)
print(f"Flow D helper call inserted at line {insert_pos + 1}")

text = "".join(lines)
assert "namespace PeakCan.Host.App.Composition;" in text
assert "public partial class AppHostBuilder" in text

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")