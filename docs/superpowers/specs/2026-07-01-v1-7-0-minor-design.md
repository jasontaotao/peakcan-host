# v1.7.0 MINOR — V8 Sandbox Hardening (Design)

**Status**: Draft 2026-07-01
**Target ship**: v1.7.0 MINOR
**Closes**: v1.6.0 MINOR #1 (V8 sandbox hardening) — 13-release-note carry-over
**Branch**: TBD (off remote main @ `ee7994a`)
**Size**: MINOR (3 items: 1 production code + 1 production code + 1 tests-only)

## Context

V8 sandbox hardening has been a deferred v1.6.0 MINOR item across 13 release notes (v1.4.0 through v1.6.10). The 2026-06-22 scripting-engine-design spec §3.3 + §10 promised "no `eval()`/`Function()` by default" + "Whitelist-only API" + path-normalization defense-in-depth, but Phase 2.5 exploration reveals significant implementation drift:

- **No resource caps** — V8 isolate uses ClearScript defaults (no MaxHeapSize, no MaxNewSpaceSize). A malicious script can OOM the process before the 60s CT-based timeout fires.
- **`eval` / `Function` not disabled** — V8's built-ins work inside the isolate despite spec promise.
- **Host objects exposed as concrete types** — CanApi (with `Dispose()`) and DbcApi (with `Dispose()`) injected wholesale. Any future public method becomes auto-reachable from JS — quiet attack-surface expansion.
- **8 tests, all happy/error single-script paths** — zero security regression coverage.

v1.7.0 MINOR addresses the architectural gap with a focused 3-item scope: runtime resource caps + script-facing interface isolation + security regression test suite.

## Goal

1. **Item 1**: Cap V8 isolate memory via `V8Runtime(MaxHeapSize, MaxNewSpaceSize, MaxOldSpaceSize)`, configurable via DI
2. **Item 2**: Restrict script-visible host-object surface to `IScriptCanApi` + `IScriptDbcApi` interfaces (frozen attack surface, no `Dispose`)
3. **Item 3**: Add security regression test suite (memory exhaustion + concurrent scripts + surface restriction + eval/Function exposure)

## Approach

### Item 1 — V8Runtime + resource caps (mirror DbcOptions config-binding pattern)

**New file**: `src/PeakCan.Host.App/Services/Scripting/ScriptEngineOptions.cs`
```csharp
namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// v1.7.0 MINOR Item 1: V8 isolate resource caps. Bound from
/// <c>Script:MaxHeapSizeMB</c> in <c>appsettings.json</c> via AppHostBuilder.
/// </summary>
/// <param name="MaxHeapSizeMB">
/// Hard cap on V8 isolate heap in megabytes. Default 64 MB (per V8 default
/// guidance for sandboxed scripts; well above current test scripts which
/// peak around 1-2 MB). <c>0</c> = use ClearScript default (no cap).
/// </param>
/// <param name="MaxNewSpaceSizeMB">
/// V8 new-space (young generation) cap in megabytes. Default 16 MB.
/// <c>0</c> = use ClearScript default.
/// </param>
/// <param name="MaxOldSpaceSizeMB">
/// V8 old-space (tenured generation) cap in megabytes. Default 48 MB.
/// <c>0</c> = use ClearScript default.
/// </param>
internal sealed record ScriptEngineOptions(
    int MaxHeapSizeMB = 64,
    int MaxNewSpaceSizeMB = 16,
    int MaxOldSpaceSizeMB = 48)
{
    /// <summary>Default limits per spec: 64 MB heap, 16 MB new, 48 MB old.</summary>
    public static ScriptEngineOptions Default { get; } = new();
}
```

**Modify**: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` — ctor adds `ScriptEngineOptions options` param; `CreateEngine` constructs `V8Runtime` and passes to `V8ScriptEngine`.

**Modify**: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` — add `ScriptEngineOptions` DI registration; update `ScriptEngine` factory to inject options.

**Modify**: `appsettings.json` — add `"Script": { "MaxHeapSizeMB": 64 }` documentation block.

### Item 2 — Script-facing interfaces (IScriptCanApi + IScriptDbcApi)

**New files**:
- `src/PeakCan.Host.App/Services/Scripting/IScriptCanApi.cs`
- `src/PeakCan.Host.App/Services/Scripting/IScriptDbcApi.cs`

**IScriptCanApi** (minimal script-visible surface, excludes `Dispose`):
```csharp
namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// v1.7.0 MINOR Item 2: minimal script-visible surface of CanApi.
/// Excludes <c>Dispose</c> + IFrameSink implementation members to prevent
/// scripts from disposing the API or interfering with non-script consumers.
/// </summary>
public interface IScriptCanApi
{
    Task SendAsync(CanFrame frame);
    bool IsConnected { get; }
    string GetChannelId();
    void OnFrame(Action<CanFrame> handler);
    void OffFrame(Action<CanFrame> handler);
    void OnMessage(Action<string> handler);
    void OffMessage(Action<string> handler);
}
```

**IScriptDbcApi** (minimal script-visible surface, excludes `Dispose`):
```csharp
namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// v1.7.0 MINOR Item 2: minimal script-visible surface of DbcApi.
/// Excludes <c>Dispose</c> + private event handlers.
/// </summary>
public interface IScriptDbcApi
{
    Task<object> Load(string path);
    object? Decode(CanFrame frame);
    object? GetSignal(string messageName, string signalName);
    object[] GetMessages();
}
```

**Modify**: `CanApi` — `public sealed partial class CanApi : IScriptCanApi` (verify existing public surface matches interface).
**Modify**: `DbcApi` — `public sealed partial class DbcApi : IScriptDbcApi` (same verification).
**Modify**: `ScriptEngine.cs` line 246/252 — inject interface via `_engine.AddHostObject("can", (object)_canApi)` (ClearScript will marshal as interface if concrete type implements it).

### Item 3 — Security regression tests (new test file)

**New file**: `tests/PeakCan.Host.App.Tests/Services/Scripting/ScriptEngineSecurityTests.cs`

Tests:
1. `RunAsync_MemoryExhaustionScript_Returns_GracefulError` — script `var a=[]; while(true) a.push("x".repeat(1e6))` → `ScriptResult.Success=false` + ErrorType=Runtime within timeout
2. `RunAsync_EvalExpression_IsRejected` — script `eval("1+1")` → either throws or returns non-result (ClearScript's eval IS available but we verify it doesn't leak host objects)
3. `RunAsync_FunctionConstructor_IsRejected` — script `new Function("return can.IsConnected")()` → same as eval
4. `RunAsync_ConcurrentScripts_NoStateLeak` — 2 scripts run in parallel via `Task.WhenAll`, verify each script's host-object mutations don't leak to the other
5. `CanApi_DoesNotExpose_Dispose_ToScripts` — reflect on `IScriptCanApi` to verify it has no `Dispose` member
6. `DbcApi_DoesNotExpose_Dispose_ToScripts` — same for `IScriptDbcApi`

## Decisions

### D1 — Item 1 scope: V8Runtime + 3 memory caps (no other V8ScriptEngineFlags)

Chose over adding `DisableEval`, `DisableValueTaskPromise`, `EnableRemoteDebugging = false` etc.

Rationale:
- `DisableEval` doesn't exist in ClearScript 7.4.5's `V8ScriptEngineFlags` enum
- `DisableValueTaskPromise` is 7.5+ feature (not available)
- `EnableRemoteDebugging` defaults to false already
- The real eval defense is the script-facing interface isolation (Item 2) — scripts can `eval("can.IsConnected")` but only get the interface methods, not `Dispose`
- Resource caps are the highest-impact, lowest-risk hardening (one well-tested knob)

### D2 — Item 2 scope: interfaces over CanApi/DbcApi, NOT a new restriction layer

Chose over (a) wrapper classes that re-expose methods, (b) `V8Runtime.AddConstraint`, (c) custom marshaling.

Rationale:
- Interfaces are the simplest "freeze surface" mechanism
- CanApi/DbcApi already have public methods that scripts need; no functionality change
- Wrapper classes double the API surface (confusion)
- `V8Runtime.AddConstraint` is for runtime heap/execution limits, not API surface (per ClearScript docs)

### D3 — Item 3 scope: 6 security tests, all in one new file

Chose over (a) per-test in existing `ScriptEngineTests.cs`, (b) split across multiple test files.

Rationale:
- Security tests are conceptually a separate suite (visual + grep-friendly)
- Existing `ScriptEngineTests.cs` is happy/error paths; security is distinct
- One file is easier to audit for completeness

### D4 — DI binding: factory-closure over IConfiguration (mirror DbcOptions, PathOptions)

Per established pattern. NOT `IOptions<>`.

```csharp
// AppHostBuilder.cs after PathOptions registration
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>().GetSection("Script");
    return new ScriptEngineOptions(
        MaxHeapSizeMB: config.GetValue<int?>("MaxHeapSizeMB") ?? 64,
        MaxNewSpaceSizeMB: config.GetValue<int?>("MaxNewSpaceSizeMB") ?? 16,
        MaxOldSpaceSizeMB: config.GetValue<int?>("MaxOldSpaceSizeMB") ?? 48);
});
```

### D5 — Item 3 eval/Function test approach: assert rejection, not literal disable

Since ClearScript 7.4.5 doesn't expose a `DisableEval` flag, we cannot literally disable `eval`/`Function`. Instead, Item 3 tests assert that even if a script uses `eval` or `new Function(...)`, it cannot reach `Dispose()` or other non-interface members. The assertion is on **attack surface reduction**, not literal eval ban.

This matches D1's rationale: interface isolation is the real defense.

## Files affected

| File | Type | Change |
|---|---|---|
| `src/PeakCan.Host.App/Services/Scripting/ScriptEngineOptions.cs` | NEW | record + Default static |
| `src/PeakCan.Host.App/Services/Scripting/IScriptCanApi.cs` | NEW | minimal CanApi surface |
| `src/PeakCan.Host.App/Services/Scripting/IScriptDbcApi.cs` | NEW | minimal DbcApi surface |
| `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` | edit | ctor +options param; V8Runtime construction; inject interface-typed host objects |
| `src/PeakCan.Host.App/Services/Scripting/CanApi.cs` | edit | `: IScriptCanApi`; verify public surface matches |
| `src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` | edit | `: IScriptDbcApi`; verify public surface matches |
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | edit | ScriptEngineOptions DI registration; ScriptEngine factory uses options |
| `appsettings.json` | edit | +`Script:MaxHeapSizeMB` documentation |
| `tests/PeakCan.Host.App.Tests/Services/Scripting/ScriptEngineSecurityTests.cs` | NEW | 6 security tests |
| `docs/release-notes-v1.7.0.md` | NEW | per established MINOR template |

## Tests

App.Tests `ScriptEngineSecurityTests.cs` (6 tests):
1. `RunAsync_MemoryExhaustionScript_Returns_GracefulError` (Item 1)
2. `RunAsync_EvalExpression_DoesNotExpose_NonInterface_Members` (Item 3, see D5)
3. `RunAsync_FunctionConstructor_DoesNotExpose_NonInterface_Members` (Item 3)
4. `RunAsync_ConcurrentScripts_NoStateLeak` (Item 3)
5. `IScriptCanApi_Omits_Dispose_Method` (Item 2, declarative)
6. `IScriptDbcApi_Omits_Dispose_Method` (Item 2, declarative)

Test delta: App 429 → 435 (+6 net). Total 863 → 869 (+6 net).

## Out of scope

- **Structured error reporting (V8Exception types + line/column)** — defer to v1.7.1 PATCH
- **onInit failure flips Success to false** — defer to v1.7.1 PATCH
- **ClearScript 7.5+ bump** — defer (gap observation, not blocking)
- **OEM `IKeyDerivationAlgorithm` concrete** — separate v1.7.1 MINOR (after this MINOR)
- **Disabling `eval` literally** — ClearScript 7.4.5 doesn't expose the flag; interface isolation (Item 2) is the real defense
- **Configurable resource caps beyond heap** — V8Runtime supports MaxStackSize etc., defer unless security review requests

## Open questions

None.

## Cross-references

- `peakcan-host-v1-6-10-shipped.md` (memory) — closes 13-release-note carry-over
- `peakcan-host-v1-6-9-shipped.md` (memory) — referenced in v1.6.0 MINOR decomposition table
- `2026-06-22-scripting-engine-design.md` — original V8 spec (§3.3 eval promise + §10 risk mitigations)
- `ScriptEngine.cs:222` — current `V8ScriptEngineFlags.DisableGlobalMembers` (only flag set)
- `AppHostBuilder.cs:283-309` — ScriptEngine DI wiring (mirror for new options)