# Release Notes v3.29.0 — ScriptEngine god-class refactor (MINOR)

**Released:** 2026-07-12
**Tag:** v3.29.0
**Branch:** `feature/w14-script-engine-god-class`
**Parent:** v3.28.0 MINOR (`f29453f` on origin/main)

## Why this MINOR

`src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` had grown to **548 LoC** as of v3.28.0 — at 68.5% of the 800 LoC Round-1 ceiling. Single instance class implementing `IDisposable` with **16 methods** (2 ctors + RunAsync + Stop + InterruptEngine + ExecuteScript + CreateEngine + EmitOutput + IsResourceLimit + Dispose + 3 LoggerMessage partials) + 1 property `IsRunning` + 1 event `OutputReceived` + 4 sibling types (ScriptResult record + ScriptErrorType enum + ScriptOutputLine record + ScriptOutputLevel enum).

This is the **11th god-class refactor** in the project (W3-W14 series), the **5th App layer** god-class (W3 TraceViewerViewModel + W4 AppShellViewModel + W5 SignalViewModel + W6 SendViewModel + W7 MultiFrameSendViewModel + W8 TraceChartViewModel + W11 AppHostBuilder + W14 ScriptEngine). **First App-layer god-class with external native binding lifecycle** (V8ScriptEngine = unmanaged resource via ClearScript 7.4.5). Validates the partial-class split pattern works for: instance class with IDisposable + thread-safety + generation counter + external native resource lifecycle + LoggerMessage partials + sibling record/enum types (sister of W12 UdsClient but with V8 binding).

## LoC trajectory (W8.5 D7 CONFIRMED formula)

All 3 transitions **EXACT match** (W8.5 D7 now 11-locked across W12 + W13 + W14):

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | ExecutionLifecycle (RunAsync + Stop + InterruptEngine + ExecuteScript) | 111-358 | 248 | 301 |
| T2 | CreateEngine | 124-201 | 78 | 224 |
| T3 | ScriptHelpers (EmitOutput + IsResourceLimit only -- Dispose stays in main per D2) | 125-131 + 133-150 | 25 | 200 |
| **Total** | -- | -- | **351** | **200** |

**Net**: 548 → 200 LoC main file (**-348 LoC, -63.5%**). Total project LoC unchanged (~548 across main + 3 partials).

## What this MINOR does

### Refactor — ScriptEngine split into 3 partial-class files

The class was already `public sealed partial class ScriptEngine : IDisposable` at line 26 (modifier pre-existed for future split, per W13 D7 sister observation `static-partial-already-present-implies-class-split-was-always-intended`). The main file keeps: 8 readonly fields + 4 mutable state fields + 1 `object _lock` + 1 event + 1 property + 2 ctors + 3 `[LoggerMessage]` partial declarations + 4 sibling type declarations.

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `ScriptEngine/ExecutionLifecycleFlow.cs` | A — ExecutionLifecycle cluster | ~225 | RunAsync, Stop, InterruptEngine, ExecuteScript |
| `ScriptEngine/CreateEngineFlow.cs` | B — V8 engine creation | ~90 | CreateEngine |
| `ScriptEngine/ScriptHelpersFlow.cs` | C — Stateless helpers | ~32 | EmitOutput, IsResourceLimit |

Each partial file declares `public sealed partial class ScriptEngine { ... }` and adds the flow's methods verbatim. **Flow A kept all 4 lifecycle methods together** per W14 D2 + W3 R3 sister lesson (mutable-state coupling on `_engine` + `_executionCts` + `_executionTask` + `_generation`). **Dispose stays in main** per W14 D2 (`Dispose` calls `Stop()` + `_executionCts?.Dispose()` — both field accesses warrant state-ownership co-location). **3 `[LoggerMessage]` partial declarations stay in main** per W10 + W11 + W12 + W13 sister (source generator scope requirement).

### Verification

- `dotnet build src/PeakCan.Host.App/`: **0 errors, 0 warnings**
- `dotnet test --filter "~ScriptEngine"`: **25 / 25 PASS** (count unchanged from v3.28.0 baseline)
- `dotnet test` (full solution): **1339 PASS, 5 SKIP, 0 FAIL** — 89 Infrastructure + 801 App + 449 Core

## Lessons applied

- **W8.5 D7 CONFIRMED** LoC correction formula — all 3 transitions EXACT match.
- **W3 R3 + W14 D2** execution-lifecycle-cluster sister-lesson applied: RunAsync + Stop + InterruptEngine + ExecuteScript kept together in Flow A (mutable-state coupling on `_engine` + `_generation`).
- **W10 D5 + W11 D5 + W12 D5** sister-lessons: Dispose + fields + ctors + 3 LoggerMessage partials + 4 sibling types stay in main (state-ownership + source-generator scope + type-declaration-with-class).
- **W11 R3** helper-extract-on-demand — verified NOT needed: `ExecuteScript` 155 LoC stays inline per W14 D8 (sister of W12 D7). One-method-one-purpose (execute script); body is one continuous try/catch/finally.
- **W12 T4** xmldoc-grep fix lesson — verified NOT applicable (no source-path grep tests for ScriptEngine per W12 D8 + W13 D8 sister observation).
- **W13 T1 2/3 loose-assertion pattern** applied at all 3 script-assertion sites: accept ±1 LoC tolerance for `wc -l` vs Python `splitlines(keepends=True)` off-by-one on un-trailing-newline files.

## New sibling-lesson candidates (per W14 R3 + R5 + T3 observations)

- **`execution-lifecycle-cluster-must-not-be-split-across-partials`** (1/3) — W14 D2/T1 confirmed: lifecycle methods sharing mutable execution state must stay in one partial even if it spans a large LoC range. W3 R3 first observation; W14 T1 second; awaits 1 more for promotion.
- **`v8-native-binding-lifecycle-survives-partial-extraction-verbatim`** (1/3) — `engine.Execute("if (typeof onDispose === 'function') onDispose();");` + `engine.Dispose()` + CAS-protected `_engine` null in ExecuteScript's finally — all preserved verbatim. Confirms the un-managed-resource lifecycle pattern is partial-extraction-safe.
- **`scriptengine-dispose-stayed-in-main-when-helpers-moved`** (1/3) — W14 T3 observation: when 3 small helpers moved in Flow C, Dispose was kept in main because it accesses `_executionCts` field and calls `Stop()` (RunAsync's inverse — field ownership co-location).

All 3 await 1 more observation (W15+) for promotion.

## What stays the same

- Public API surface — `RunAsync` + `Stop` + `Dispose` + `IsRunning` property + `OutputReceived` event + 2 ctors callable with identical signatures from `ScriptEngine`.
- Test count unchanged (25 ScriptEngine tests pre + post; 1339 full-solution 0 fails).
- DI registration unchanged (`AppHostBuilder` Flow C AppServicesFlow registers `ScriptEngine` as singleton; partial-class transparent).
- V8 native resource lifecycle: `engine.Dispose()` call sequence in ExecuteScript's finally is identical (verbatim extraction preserves the call sequence; CAS-protected `_engine` null-out + `ScriptConsole.CurrentEngine = null` ordering maintained).

## Next steps (post-ship)

- **W14.5 vault-only PATCH** — candidate lesson-promotion if any 1/3 candidate reaches 3/3 confirmation in W15+.
- **W15** — next god-class refactor candidate: `ReplayTimeline.cs` (469 LoC `internal sealed partial class ReplayTimeline` — `internal` visibility variant never tested yet, plus the `Timer`-based playback scheduler with Play/Pause/Resume/Seek/SetSpeed/Stop state-machine) or a new candidate.
