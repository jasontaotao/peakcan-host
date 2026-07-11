"""Delete Flow B (CoreInfrastructure) from AppHostBuilder.cs (Task 2).

W11 helper extraction pattern (D5):
- Replace Core infrastructure section in Build() body with a call to
  RegisterCoreInfrastructure(builder.Services) helper.
"""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/Composition/AppHostBuilder.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 109-146: Core infrastructure section.
# Expected LoC at Task 2 start: 682 (post-Task-1).
DELETIONS = [(109, 146, "Core infrastructure section (ChannelRouter through PcanReader)")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 682, f"Expected 682 LoC at Task 2 start (post-Task-1), got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Insert helper call + marker BEFORE "// App services" section
new_helper_call = """        // === Flow B: Core infrastructure extracted to AppHostBuilder/CoreInfrastructureFlow.cs (W11 Task 2) ===
        RegisterCoreInfrastructure(builder.Services);

"""

insert_pos = None
for i, ln in enumerate(lines):
    if "// App services" in ln and "TraceService" not in ln:
        insert_pos = i
        break

assert insert_pos is not None, "Could not find // App services marker"

lines.insert(insert_pos, new_helper_call)
print(f"Flow B helper call inserted at line {insert_pos + 1}")

text = "".join(lines)
assert "namespace PeakCan.Host.App.Composition;" in text
assert "public partial class AppHostBuilder" in text
# Helper call IS in main file (the new Build signature calls it)
# Helper body is in partial file (CoreInfrastructureFlow.cs)
# Both are correct — partial-class visibility

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")