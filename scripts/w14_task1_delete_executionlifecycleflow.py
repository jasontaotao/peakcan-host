"""Delete Flow A (ExecutionLifecycle: RunAsync+Stop+InterruptEngine+ExecuteScript) from ScriptEngine.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
# W13 T1 2/3 lesson: splitlines(keepends=True) vs wc -l off-by-one on un-trailing-newline files.
print(f"Original line count: {original_count}")
assert original_count in (548, 549), f"Expected 548/549 LoC at Task 1 start, got {original_count}"

# 1 contiguous range in 548-LoC file:
# (1) RunAsync (111) + Stop (161) + InterruptEngine (179) + ExecuteScript (205-358).
# Looking at the grep above, line 358 is end of ExecuteScript's finally closing brace.
# Range 111-358 = 248 LoC contiguous block.
DELETIONS = [
    (111, 358, "RunAsync + Stop + InterruptEngine + ExecuteScript (execution lifecycle cluster, ~248 LoC)"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 548 - 248 + 1 = 301; with loose assertion ±2 LoC tolerance.
expected_pre_marker = original_count - 248
print(f"Pre-marker LoC: {expected_pre_marker}")
assert expected_pre_marker in (300, 301), f"Expected 300-301 LoC pre-marker, got {expected_pre_marker}"

text = "".join(lines)

# Critical invariants -- public API + state preserved:
assert "namespace PeakCan.Host.App.Services.Scripting;" in text
assert "public sealed partial class ScriptEngine : IDisposable" in text, "Outer class must stay partial"
# Fields preserved (state ownership)
assert "private readonly ILogger<ScriptEngine> _logger;" in text
assert "private V8ScriptEngine? _engine;" in text
assert "private CancellationTokenSource? _executionCts;" in text
assert "private Task? _executionTask;" in text
assert "private long _generation;" in text
# ctors preserved
assert text.count("public ScriptEngine(") >= 1 or text.count("internal ScriptEngine(") >= 1
# OutputReceived event preserved
assert "public event Action<ScriptOutputLine>? OutputReceived;" in text
# IsRunning property preserved
assert "public bool IsRunning" in text
# Dispose preserved (in main, not in Flow C per D2)
assert "public void Dispose()" in text
# 3 LoggerMessage partial declarations preserved (in main, not in any Flow per W10+W11+W12+W13 sister)
assert "LogInterruptFailed" in text
assert "LogOnInitError" in text
assert "LogScriptError" in text
# ScriptResult record preserved
assert "public sealed record ScriptResult" in text
# ScriptErrorType enum preserved
assert "public enum ScriptErrorType" in text
# ScriptOutputLine record preserved
assert "public sealed record ScriptOutputLine" in text

# ExecutionLifecycle methods GONE from main:
assert "public async Task<ScriptResult> RunAsync" not in text
assert "public void Stop()" not in text
assert "private void InterruptEngine()" not in text
assert "private void ExecuteScript(string script" not in text

# Other flows preserved (B + C haven't moved yet):
assert "private V8ScriptEngine CreateEngine(CancellationToken ct)" in text
assert "internal void EmitOutput(ScriptOutputLine line)" in text
assert "private static bool IsResourceLimit(ScriptEngineException ex)" in text

# Marker - insert before closing brace of class
marker = "    // === Flow A methods moved to ScriptEngine/ExecutionLifecycleFlow.cs (W14 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow A marker inserted before line {i + 1} (class closing brace)")
        break

expected_post = expected_pre_marker + 1
assert len(lines) in (expected_post - 1, expected_post), f"Expected {expected_post - 1}/{expected_post} LoC post-marker, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
