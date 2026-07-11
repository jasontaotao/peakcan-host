"""Delete Flow A (Logging) from AppHostBuilder.cs (Task 1).

This is the FIRST W11 task. It uses the HELPER METHOD EXTRACTION pattern
(D5): replace the Logging section in Build() body with a call to a new
private helper method ConfigureLoggingAndBuilder(out IHostBuilder builder)
defined in the partial file LoggingFlow.cs.

Build() body BEFORE Task 1:
    public IHost Build()
    {
        // ... 67 lines of Logging setup ...
    }

Build() body AFTER Task 1:
    public IHost Build()
    {
        ConfigureLoggingAndBuilder(out var builder);
        // ... rest of Build body ...
    }
"""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/Composition/AppHostBuilder.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 96-162: Logging setup section in Build() body.
# Replace this with the helper-method call.
ORIGINAL_SECTION = """    public IHost Build()
    {
        // v3.9.0 MINOR P5: create the IHostBuilder FIRST so its"""

# Find the start of the section. The Build() opening brace is at line 96.
# We want to delete from line 96 (Build method signature) through line 162
# (last line of Logging: builder.Logging.ClearProviders().AddSerilog(...))
# and replace with the helper call.

# Actually we need a more careful approach: replace lines 96-162 with the
# new Build() method signature + helper call + opening brace.

# Let's identify the range: lines 96-162 inclusive = 67 lines
DELETIONS = [(96, 162, "Logging setup section (Build method start through Serilog registration)")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 744, f"Expected 744 LoC at Task 1 start, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Now insert the new Build() signature + helper call at the deletion point
# (which is now at the original position since we deleted downward).
new_build_signature = """    public IHost Build()
    {
        // === Flow A: Logging setup extracted to AppHostBuilder/LoggingFlow.cs (W11 Task 1) ===
        ConfigureLoggingAndBuilder(out var builder);

"""

# Insert at the deletion point (line 96 area, which is now after delete).
# Find the position where the next flow starts (which was at line 163+).
# After deletion, the v1.5.0 comment for IConfiguration registration is
# at the line where line 164 was originally (now around line 97).
# We need to insert BEFORE that.

# Find the IConfiguration comment to know where Flow A ends and Flow B begins.
marker_text = "// v1.5.0 MINOR: expose the host's IConfiguration"
insert_pos = None
for i, ln in enumerate(lines):
    if marker_text in ln:
        insert_pos = i
        break

assert insert_pos is not None, "Could not find v1.5.0 IConfiguration marker"

# Insert the new Build signature + helper call + opening of body
lines.insert(insert_pos, new_build_signature)
print(f"New Build signature inserted at line {insert_pos + 1}")

# Insert flow marker comment at end (will be replaced by Task 2 marker later)
# Skip marker insertion for Task 1 — will be added at end of file before closing brace

text = "".join(lines)
assert "namespace PeakCan.Host.App.Composition;" in text
# Add partial modifier to outer class declaration (W10 T1 fix pattern)
text = text.replace("public class AppHostBuilder", "public partial class AppHostBuilder", 1)
# Verify helper method exists in main (will be in partial file)
# assert "ConfigureLoggingAndBuilder" in text  -- this will be false because the method is in the partial file

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")