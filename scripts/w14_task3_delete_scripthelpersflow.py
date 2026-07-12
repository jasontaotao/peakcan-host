"""Delete Flow C (ScriptHelpers: EmitOutput + IsResourceLimit) from ScriptEngine.cs (Task 3)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
# W13 T1 2/3 lesson: loose assertion.
print(f"Original line count: {original_count}")
assert original_count in (223, 224, 225), f"Expected 223/224/225 LoC at Task 3 start (post-T2), got {original_count}"

# Per W14 D2: Dispose STAYS in main (state-ownership with _executionCts field).
# Move ONLY: EmitOutput (with xmldoc) + IsResourceLimit (with xmldoc).
# Range 1: lines 125-131 = 7 LoC (EmitOutput xmldoc + method body)
# Range 2: lines 133-150 = 18 LoC (IsResourceLimit xmldoc + method body)
DELETIONS = [
    (125, 131, "EmitOutput event invoke (with xmldoc)"),
    (133, 150, "IsResourceLimit V8 violation heuristic (with xmldoc)"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

total_deleted = 0
for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    count = end_line - start0
    total_deleted += count
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({count} lines)")
    del lines[start0:end_line]

expected_pre_marker = original_count - total_deleted
print(f"Pre-marker LoC: {expected_pre_marker}")
assert expected_pre_marker in (199, 200, 201), f"Expected 199-201 LoC pre-marker, got {expected_pre_marker}"

text = "".join(lines)
assert "public sealed partial class ScriptEngine : IDisposable" in text
# ExecutionLifecycle + CreateEngine GONE
assert "public async Task<ScriptResult> RunAsync" not in text
assert "private V8ScriptEngine CreateEngine" not in text
# ScriptHelpers GONE
assert "internal void EmitOutput" not in text
assert "private static bool IsResourceLimit" not in text
# Dispose STAYS in main per W14 D2
assert "public void Dispose()" in text
# 3 LoggerMessage partials STAY in main per W14 D5 sister
assert "LogInterruptFailed" in text
assert "LogOnInitError" in text
assert "LogScriptError" in text

# Marker
marker = "    // === Flow C methods moved to ScriptEngine/ScriptHelpersFlow.cs (W14 Task 3) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow C marker inserted before line {i + 1}")
        break

expected_post = expected_pre_marker + 1
assert len(lines) in (expected_post - 1, expected_post), f"Expected {expected_post - 1}/{expected_post} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
