"""Delete Flow G (Window + hosted services) from AppHostBuilder.cs (Task 6 — LAST extraction).

W11 helper extraction pattern (D5):
- Replace Window + hosted services section in Build() body with a call to
  RegisterWindowAndHostedServices(builder.Services) helper.
- Keep the final `return builder.Build();` line in main Build body.
"""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/Composition/AppHostBuilder.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 311-325: Window + hosted services section (AppShell + SinkWiringService).
# Keep line 326 (return builder.Build();) in main Build body.
# Expected LoC at Task 6 start: 328 (post-Task-5).
DELETIONS = [(311, 325, "Window + hosted services section (AppShell + SinkWiringService)")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 328, f"Expected 328 LoC at Task 6 start (post-Task-5), got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Insert helper call + marker BEFORE the return builder.Build() line
new_helper_call = """        // === Flow G: Window + hosted services extracted to AppHostBuilder/WindowAndHostedServicesFlow.cs (W11 Task 6 — LAST extraction) ===
        RegisterWindowAndHostedServices(builder.Services);

"""

# Find the return builder.Build() line
insert_pos = None
for i, ln in enumerate(lines):
    if "return builder.Build();" in ln:
        insert_pos = i
        break

assert insert_pos is not None, "Could not find return builder.Build()"

lines.insert(insert_pos, new_helper_call)
print(f"Flow G helper call inserted at line {insert_pos + 1}")

text = "".join(lines)
assert "namespace PeakCan.Host.App.Composition;" in text
assert "public partial class AppHostBuilder" in text
assert "return builder.Build();" in text  # must be kept in main Build body

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")