"""Delete Flow B (CreateEngine) from ScriptEngine.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
# W13 T1 2/3 lesson: loose assertion.
print(f"Original line count: {original_count}")
assert original_count in (300, 301, 302), f"Expected 300/301/302 LoC at Task 2 start (post-T1), got {original_count}"

# Range: CreateEngine lines 124-201 = 78 LoC (xmdloc + body + close brace).
DELETIONS = [
    (124, 201, "CreateEngine V8 host-object wiring (~78 LoC)"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

expected_pre_marker = original_count - 78
print(f"Pre-marker LoC: {expected_pre_marker}")
assert expected_pre_marker in (222, 223, 224), f"Expected 222-224 LoC pre-marker, got {expected_pre_marker}"

text = "".join(lines)
assert "public sealed partial class ScriptEngine : IDisposable" in text
# ExecutionLifecycle stays gone
assert "public async Task<ScriptResult> RunAsync" not in text
# CreateEngine GONE
assert "private V8ScriptEngine CreateEngine" not in text
# Other flows preserved (Flow C hasn't moved yet)
assert "internal void EmitOutput" in text
assert "private static bool IsResourceLimit" in text
assert "public void Dispose()" in text

# Marker
marker = "    // === Flow B methods moved to ScriptEngine/CreateEngineFlow.cs (W14 Task 2) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow B marker inserted before line {i + 1}")
        break

expected_post = expected_pre_marker + 1
assert len(lines) in (expected_post - 1, expected_post), f"Expected {expected_post - 1}/{expected_post} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
