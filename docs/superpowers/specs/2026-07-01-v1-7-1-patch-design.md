# v1.7.1 PATCH — 3-item Tidy (Design)

**Status**: Draft 2026-07-01
**Target ship**: v1.7.1 PATCH
**Closes**: v1.7.0 MINOR MEDIUM 2 deferred (IScriptCanApi ergonomics) + v1.7.0 spec out-of-scope (structured error reporting + onInit failure flip) + housekeeping carry-over (4 untracked docs + MEMORY.md rotation)
**Branch**: TBD (off remote main @ `5c522ca`)
**Size**: Tidy PATCH (3 items: 2 production code + 1 docs-only)

## Context

v1.7.0 MINOR closed the 13-release-note v1.6.0 MINOR V8 sandbox hardening carry-over. The ship review caught 2 MEDIUMs: MEDIUM 1 (ScriptEngineOptions XML doc) inline-fixed; MEDIUM 2 (IScriptCanApi ergonomics — no `Send(CanFrame)` overload, no property-based `IsConnected`) deferred to v1.7.1 PATCH per v1.7.0 spec §"Out of scope".

Two additional v1.7.0 spec out-of-scope items belong to the same V8 error-handling surface:
- **Structured error reporting** (V8Exception types + line/column) — current code does string-match on "interrupted" message (fragile; intended typed catch per `ScriptEngine.cs:213-215` original design)
- **onInit failure flip `Success` to `false`** — current code logs but doesn't surface as ScriptResult failure (line 195-199)

Plus housekeeping carry-over:
- 4 untracked design/plan docs from v1.6.10 + v1.7.0 cycles (per v1.6.9 PATCH Item 3 Option B pattern)
- MEMORY.md size ~47.7KB (well past 24.4KB warning; only 3.5KB of it was loaded in this session per system reminder)

v1.7.1 PATCH = 3-item Tidy closing all four.

## Goal

1. Close v1.7.0 MEDIUM 2 deferred (IScriptCanApi ergonomics)
2. Close 2 v1.7.0 spec out-of-scope items (structured error + onInit flip)
3. Close housekeeping carry-over (4 docs + MEMORY.md rotation)

## Approach

### Item 1 — IScriptCanApi ergonomics (closes v1.7.0 MEDIUM 2)

**Modify**: `src/PeakCan.Host.App/Services/Scripting/IScriptCanApi.cs`

Add to interface:
```csharp
/// <summary>
/// v1.7.1 PATCH Item 1: ergonomic property access for IsConnected.
/// Backed by <see cref="CanApi.IsConnected"/> method via explicit
/// interface implementation. Scripts prefer property access for
/// read-only state queries.
/// </summary>
bool IsConnected { get; }

/// <summary>
/// v1.7.1 PATCH Item 1: ergonomic overload for sending a pre-built
/// <see cref="CanFrame"/>. Convenient for scripts that decode
/// received frames and re-send them with modifications.
/// </summary>
Task<bool> Send(CanFrame frame);
```

**Modify**: `src/PeakCan.Host.App/Services/Scripting/CanApi.cs`

Add explicit interface implementations (additive, no behavior change to existing method-based API):
```csharp
// v1.7.1 PATCH Item 1: ergonomic property + overload (additive).
// v1.7.3 PATCH retroactive correction: CanFrame is readonly record
// struct (value type), so ArgumentNullException.ThrowIfNull would be
// a CA2264 no-op. CanFrame.Data is ReadOnlyMemory<byte> (not byte[]),
// so .ToArray() converts ROM<byte> → byte[] to match CanApi.Send(int,
// byte[], bool, bool) signature. Both adaptations applied at v1.7.1
// implementation time (CS1503 caught at first build).
bool IScriptCanApi.IsConnected => IsConnected();

async Task<bool> IScriptCanApi.Send(CanFrame frame)
{
    return await Send(
        (int)frame.Id.Raw,
        frame.Data.ToArray(),
        fd: (frame.Flags & FrameFlags.Fd) != 0,
        extended: frame.Id.Format == FrameFormat.Extended).ConfigureAwait(false);
}
```

**Test** (1 test in `ScriptEngineTests.cs`):
- `IScriptCanApi_Exposes_IsConnected_Property_And_Send_Overload` — reflect on `IScriptCanApi` to verify both new members present; smoke-call the explicit implementation to verify it returns same as method.

**Note**: Add 1 declarative test + 1 behavioral test (2 net).

### Item 2 — Structured error reporting + onInit failure flip (closes v1.7.0 spec out-of-scope)

**Modify**: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs`

Replace generic `catch (Exception ex)` at line 211-225 with typed catch:
```csharp
catch (V8ScriptInterruptedException)
{
    tcs.TrySetResult(new ScriptResult(
        Success: false,
        Error: "Script execution was interrupted",
        ErrorType: ScriptErrorType.Timeout));
}
catch (V8RuntimeViolationException ex)
{
    LogScriptError(_logger, ex);
    tcs.TrySetResult(new ScriptResult(
        Success: false,
        Error: ex.Message,
        ErrorType: ScriptErrorType.ResourceLimit));
}
catch (V8Exception ex)
{
    LogScriptError(_logger, ex);
    tcs.TrySetResult(new ScriptResult(
        Success: false,
        Error: ex.Message,
        ErrorType: ScriptErrorType.Runtime));
}
catch (Exception ex)
{
    LogScriptError(_logger, ex);
    tcs.TrySetResult(new ScriptResult(
        Success: false,
        Error: ex.Message,
        ErrorType: ScriptErrorType.Runtime));
}
```

Add `ScriptErrorType.ResourceLimit` to the enum (line 363-371):
```csharp
/// <summary>Script exceeded V8 resource limits (heap/generation caps).</summary>
ResourceLimit,
```

Add `using Microsoft.ClearScript;` at top (for `V8Exception` base; `V8ScriptInterruptedException` + `V8RuntimeViolationException` are in `Microsoft.ClearScript.V8` which is already imported via `using Microsoft.ClearScript.V8;`).

**Modify onInit handling** at line 191-199:
```csharp
// If the script defines onInit(), call it. onInit failure
// flips Success to false (v1.7.1 PATCH Item 2).
var onInitFailed = false;
try
{
    engine.Execute("if (typeof onInit === 'function') onInit();");
}
catch (Exception ex)
{
    onInitFailed = true;
    LogOnInitError(_logger, ex);
    EmitOutput(ScriptOutputLine.Error($"onInit() error: {ex.Message}"));
}

// Script completed successfully (only if onInit didn't fail).
if (!onInitFailed)
{
    tcs.TrySetResult(new ScriptResult(Success: true, Error: null, ErrorType: null));
}
```

**Tests** (2 tests in `ScriptEngineTests.cs`):
- `RunAsync_OnInit_Throws_Sets_Success_False` — define `onInit` that throws; verify `result.Success == false`
- `RunAsync_SyntaxError_Reports_Runtime_ErrorType` — script with syntax error; verify `result.ErrorType == ScriptErrorType.Runtime`

**Note**: Add 2 behavioral tests (+2 net).

### Item 3 — Housekeeping batch (closes Option B carry-over + MEMORY.md size warning)

**Files**:
- `docs/superpowers/specs/2026-06-30-v1-6-10-patch-design.md` (NEW commit, 196 lines) — v1.6.10 cycle design
- `docs/superpowers/plans/2026-06-30-v1-6-10-patch.md` (NEW commit) — v1.6.10 cycle plan
- `docs/superpowers/specs/2026-07-01-v1-7-0-minor-design.md` (NEW commit, 228 lines) — v1.7.0 cycle design
- `docs/superpowers/plans/2026-07-01-v1-7-0-minor.md` (NEW commit, 763 lines) — v1.7.0 cycle plan

**Modify**: `C:\Users\13777\.claude\projects\D--claude-proj2\memory\MEMORY.md`

Rotate to reduce size from ~47.7KB to ~22KB:
- Keep "current" (v1.7.0) detailed entry
- Move v1.6.10 to "previous" (still detailed)
- Move v1.6.9/8/7/6/5/4/3/2/1 to Historical (one-liner each, full detail in topic file)
- Consolidate "Known follow-ups" — collapse v1.6.5/6/7/8/9/10 follow-up lists into single retrospective

**Note**: 0 production code, 0 tests. Pure housekeeping. Per v1.6.9 PATCH Item 3 pattern.

## Decisions

### D1 — Item 1 explicit interface implementations, not new public members

Add `bool IScriptCanApi.IsConnected` and `Task<bool> IScriptCanApi.Send(CanFrame)` as **explicit interface implementations** to CanApi. This:
- Avoids public-surface expansion on CanApi (no risk of UI/non-script consumers seeing the new members)
- Preserves back-compat for `CanApi.IsConnected()` method consumers
- Mirrors the v1.7.0 IScriptCanApi pattern (interface-frozen surface)

### D2 — Item 1 ergonomic property `IsConnected` via forwarding expression

`bool IScriptCanApi.IsConnected => IsConnected();` is a one-line forwarder. Avoids code duplication; the property returns the same `bool` as the method.

### D3 — Item 2 typed catch ordered by specificity

`V8ScriptInterruptedException` → Timeout (was previously string-matched)
`V8RuntimeViolationException` → ResourceLimit (new; ClearScript 7.4.5 specific)
`V8Exception` (base) → Runtime (catches all ClearScript script errors with line/column in `Message`)
Generic `Exception` → Runtime (fallback, e.g. AggregateException from Task.Run)

### D4 — Item 2 onInit failure detection via local `onInitFailed` flag

Tracks failure across the try/catch. Avoids using a second `tcs.TrySetResult` (which would no-op if main path already set it). Cleanest pattern for "if main path didn't fail AND onInit didn't fail → success".

### D5 — Item 3 commit 4 untracked docs in single commit

Per v1.6.9 PATCH Item 3 pattern (`0630ad8` 10 docs single commit). One commit, one message: "docs: commit 4 untracked design/plan docs from v1.6.10 + v1.7.0 cycles".

### D6 — Item 3 MEMORY.md rotation strategy

Keep only v1.7.0 (current) + v1.6.10 (previous) detailed. Move v1.6.9 down to v1.6.1 (9 entries) to Historical one-liner. Collapse "Known follow-ups" follow-up lists (v1.6.5/6/7/8/9/10) into single line "v1.6.x ship-new follow-ups: all closed in subsequent PATCHes (see topic files)". Target: ~22KB.

## Files affected

| File | Type | Change |
|---|---|---|
| `src/PeakCan.Host.App/Services/Scripting/IScriptCanApi.cs` | edit | +2 members (IsConnected property + Send(CanFrame) overload) |
| `src/PeakCan.Host.App/Services/Scripting/CanApi.cs` | edit | +2 explicit interface implementations |
| `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` | edit | typed catch chain; onInit failure flag; new ScriptErrorType.ResourceLimit |
| `tests/.../Scripting/ScriptEngineTests.cs` | edit | +4 tests (1 declarative + 1 behavioral Item 1; 2 behavioral Item 2) |
| `docs/superpowers/specs/2026-06-30-v1-6-10-patch-design.md` | NEW commit | v1.6.10 design doc |
| `docs/superpowers/plans/2026-06-30-v1-6-10-patch.md` | NEW commit | v1.6.10 plan doc |
| `docs/superpowers/specs/2026-07-01-v1-7-0-minor-design.md` | NEW commit | v1.7.0 design doc |
| `docs/superpowers/plans/2026-07-01-v1-7-0-minor.md` | NEW commit | v1.7.0 plan doc |
| `~/.claude/projects/D--claude-proj2/memory/MEMORY.md` | edit | rotation: 9 entries to one-liner; consolidate follow-ups |

## Tests

### Item 1 (IScriptCanApi ergonomics)

`tests/PeakCan.Host.App.Tests/Services/Scripting/ScriptEngineTests.cs`:
- `IScriptCanApi_Exposes_IsConnected_Property_And_Send_Overload` — reflect on `IScriptCanApi` to verify both new members present
- `CanApi_ExplicitInterface_IsConnected_Matches_Method_Result` — smoke-call the explicit interface implementation; verify returns same as method (requires CanApi instance with mock SendService)

### Item 2 (structured error + onInit flip)

`tests/PeakCan.Host.App.Tests/Services/Scripting/ScriptEngineTests.cs`:
- `RunAsync_OnInit_Throws_Sets_Success_False` — define `onInit` that throws; verify `result.Success == false`
- `RunAsync_SyntaxError_Reports_Runtime_ErrorType` — script with syntax error; verify `result.ErrorType == ScriptErrorType.Runtime`

### Item 3 (housekeeping)

No tests.

Test delta: App 434 → 438 (+4 net). Total 871 → 875 (+4 net).

## Out of scope

- ClearScript 7.5+ bump (gap observation, deferred)
- OEM `IKeyDerivationAlgorithm` concrete (separate v1.7.1 MINOR, needs crypto review)
- DbcApi.Load CancellationToken parameter (carry-over from v1.6.8, deferred)
- Race-test flake `[Retry(3)]` reversal (v1.6.1 PATCH Decision 5, rejected)
- v1.7.0 LOW findings (FluentAssertions style drift + WpfAppTestCollection conditional + plan self-correction) — informational only
- CanApi `_options` field / further encapsulation (out of v1.7.1 scope)
- v1.6.x ship-new follow-ups consolidation (covered in MEMORY.md rotation)

## Open questions

None.

## Cross-references

- `peakcan-host-v1-7-0-minor-shipped.md` (memory) — closes v1.7.0 MEDIUM 2 + 2 spec out-of-scope
- `peakcan-host-v1-6-10-shipped.md` (memory) — pattern reference for v1.7.1 PATCH Item 1 (additive members)
- `peakcan-host-v1-6-9-shipped.md` (memory) — pattern reference for Item 3 (Option B housekeeping)
- `ScriptEngine.cs:213-215` — original design intent for typed V8 catch (string-match fallback)
- `IScriptCanApi.cs:11-13` — v1.7.0 XML doc deferral note
