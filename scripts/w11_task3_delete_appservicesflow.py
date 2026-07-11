"""Delete Flow C (AppServices) from AppHostBuilder.cs (Task 3).

W11 helper extraction pattern (D5):
- Replace App services section in Build() body with a call to
  RegisterAppServices(builder.Services) helper.
"""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/Composition/AppHostBuilder.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 114-246: App services section (TraceService through DbcSendViewModel).
# Expected LoC at Task 3 start: 647 (post-Task-2).
DELETIONS = [(114, 246, "App services section (TraceService through DbcSendViewModel)")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 647, f"Expected 647 LoC at Task 3 start (post-Task-2), got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Insert helper call + marker BEFORE "// v2.1.0 MINOR: multi-frame" or similar marker
new_helper_call = """        // === Flow C: App services extracted to AppHostBuilder/AppServicesFlow.cs (W11 Task 3) ===
        RegisterAppServices(builder.Services);

"""

insert_pos = None
for i, ln in enumerate(lines):
    if "// v2.1.0 MINOR: multi-frame" in ln:
        insert_pos = i
        break

assert insert_pos is not None, "Could not find // v2.1.0 multi-frame marker"

lines.insert(insert_pos, new_helper_call)
print(f"Flow C helper call inserted at line {insert_pos + 1}")

text = "".join(lines)
assert "namespace PeakCan.Host.App.Composition;" in text
assert "public partial class AppHostBuilder" in text

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")