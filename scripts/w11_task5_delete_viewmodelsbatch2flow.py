"""Delete Flow E (ViewModelsBatch2) from AppHostBuilder.cs (Task 5).

W11 helper extraction pattern (D5):
- Replace ViewModels batch 2 sections in Build() body with a call to
  RegisterViewModelsBatch2(builder.Services) helper.
- Two non-contiguous ranges: Range A (TraceViewer section) + Range B (Trace/Send/Dbc/SignalChart/Signal/Stats/Script VMs).
"""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/Composition/AppHostBuilder.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 2 non-contiguous ranges in post-Task-4 469-LoC file (+4 markers):
# (1) TraceViewer section: lines 120-223 (Range A)
# (2) TraceViewModel + SendViewModel + DbcViewModel + SignalChartViewModel + SignalViewModel + StatsViewModel + ScriptViewModel: lines 408-450 (Range B)
DELETIONS = [
    (120, 223, "TraceViewer section (Range A)"),
    (408, 450, "VMs Range B (TraceViewModel through ScriptViewModel)"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 469, f"Expected 469 LoC at Task 5 start (post-Task-4), got {original_count}"

max_line = max(e for _, e, _ in DELETIONS)
assert max_line <= original_count, f"Line {max_line} > file length {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Insert helper call + marker BEFORE "// v0.7.0: file dialog" section (after Range A)
new_helper_call_range_a = """        // === Flow E: ViewModels batch 2 (Range A: TraceViewer section) extracted to AppHostBuilder/ViewModelsBatch2Flow.cs (W11 Task 5) ===
        RegisterViewModelsBatch2(builder.Services);

"""

# Insert helper call + marker BEFORE "// Windows:" section (after Range B)
new_helper_call_range_b = """        // === Flow E: ViewModels batch 2 (Range B: Trace/Send/Dbc/SignalChart/Signal/Stats/Script) extracted to AppHostBuilder/ViewModelsBatch2Flow.cs (W11 Task 5) ===
        RegisterViewModelsBatch2(builder.Services);

"""

# Find Range A insertion point (just after Range A's deletion)
insert_pos_a = None
for i, ln in enumerate(lines):
    if "// v0.7.0: file dialog" in ln:
        insert_pos_a = i
        break

assert insert_pos_a is not None, "Could not find Range A marker"
lines.insert(insert_pos_a, new_helper_call_range_a)
print(f"Flow E Range A helper call inserted at line {insert_pos_a + 1}")

# Find Range B insertion point (just after Range B's deletion)
insert_pos_b = None
for i, ln in enumerate(lines):
    if "// Windows:" in ln:
        insert_pos_b = i
        break

assert insert_pos_b is not None, "Could not find Range B marker"
lines.insert(insert_pos_b, new_helper_call_range_b)
print(f"Flow E Range B helper call inserted at line {insert_pos_b + 1}")

text = "".join(lines)
assert "namespace PeakCan.Host.App.Composition;" in text
assert "public partial class AppHostBuilder" in text

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")