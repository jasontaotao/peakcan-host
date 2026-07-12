# W14 Spec — ScriptEngine god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` from 548 LoC to ~160 LoC by extracting 3 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Sister pattern to W3-W13 partial-class split. The instance `ScriptEngine` class is **already `public sealed partial class ScriptEngine : IDisposable`** at line 26 (modifier pre-exists for future split). The refactor adds 3 partial-class files. Each partial file owns one logical flow group. Main file keeps: 8 fields + 1 event + 1 property + 2 ctors + 3 `[LoggerMessage]` partials + `ScriptResult`/`ScriptErrorType`/`ScriptOutputLine`/`ScriptOutputLevel` types (4 sibling type declarations at end-of-file). Class is in App layer (WPF MVVM via CommunityToolkit.Mvvm-adjacent — though ScriptEngine uses **ClearScript V8** rather than WPF, the App-layer location binds DI registration in `AppHostBuilder` Flow C AppServicesFlow).

**Tech Stack:** C# .NET 10 + ClearScript 7.4.5 (V8 runtime), App layer (DI via AppHostBuilder). Git with LF line endings.

## Global Constraints

- **Public API unchanged.** No method signatures, properties, events, or exception types move.
- **partial-class visibility.** All private methods + private fields visible across partial files.
- **Test coverage unchanged.** No tests added, removed, or modified. **No xmldoc-grep tests** for ScriptEngine per W12 D8 sister-observation (only behavior tests via constructor calls).
- **Line-ending normalized to LF.**
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 4.** Tasks 1-3 keep `src/Directory.Build.props` at v3.28.0. Task 4 bumps to v3.29.0.
- **Branch**: `feature/w14-script-engine-god-class` (created from `main` @ `f29453f` v3.28.0).
- **Spec**: this file.

---

## Current state (548 LoC)

`src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` has:
- 1 instance `public sealed partial class ScriptEngine : IDisposable` with **16 methods** (2 ctors + RunAsync + Stop + InterruptEngine + ExecuteScript + CreateEngine + EmitOutput + IsResourceLimit + Dispose + 3 LoggerMessage partials) + 1 property `IsRunning` + 1 event `OutputReceived`
- 8 readonly fields + 3 mutable state fields (`_engine`, `_executionCts`, `_executionTask`, `_generation`) + 1 `object _lock`
- 1 const `DefaultTimeout`
- 4 sibling types outside the class: `ScriptResult` record, `ScriptErrorType` enum, `ScriptOutputLine` record, `ScriptOutputLevel` enum

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. ScriptEngine is at **68.5%** of ceiling.

## Target state (~160 LoC main + 3 partials)

```
src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs                # main file, ~160 LoC after Task 3
src/PeakCan.Host.App/Services/Scripting/ScriptEngine/                  # NEW directory
  ExecutionLifecycleFlow.cs                                              # Task 1 -- RunAsync + InterruptEngine + ExecuteScript (~225 LoC, largest, tightly coupled)
  CreateEngineFlow.cs                                                    # Task 2 -- V8 engine creation with sandboxed globals (~90 LoC)
  ScriptHelpersFlow.cs                                                   # Task 3 -- EmitOutput + IsResourceLimit + Dispose (~55 LoC)
docs/superpowers/plans/2026-07-12-script-engine-god-class-refactor.md  # NEW in Task 0
docs/release-notes-v3.29.0.md                                           # NEW in Task 4
```

**Net reduction**: 548 → ~160 LoC main file (-388 LoC, -70.8%); total lines unchanged (~548 across main + partials).

## Flow boundaries (revised: 3 partials, not 4, due to execution-state coupling)

All flows are instance methods on `ScriptEngine`. Each partial-class file declares `public sealed partial class ScriptEngine { ... }` and adds the flow's methods. Per W3 R3 sister-lesson (execution-state fields tightly coupled to RunAsync + InterruptEngine + ExecuteScript) — these three stay in **one partial** to avoid scattering coupled-lifecycle methods across files.

### Flow — Main file (declaration + state + ctors + 3 LoggerMessage partials + sibling types)

**Stays in main**:
- `using` block (line 1-4)
- Namespace + outer class xmldoc (line 6-25)
- Outer class declaration (line 26) — already partial, no change needed
- `DefaultTimeout` const (line 29)
- 8 readonly fields (lines 31-35 + 48): `_logger`, `_canApi`, `_dbcApi`, `_utilities`, `_options`, `_lock`
- 3 mutable state fields (lines 37-39): `_engine`, `_executionCts`, `_executionTask`
- `_generation` long field (line 47) — generation counter for stale-task drop (sister pattern of W12 UdsClient TCS)
- `OutputReceived` event (line 51)
- `IsRunning` property (line 54-57)
- 2 ctors (lines 64-109) — state-ownership kept here per W11 D5 sister-lesson
- `Dispose` method (line 478-482) — tightly coupled to `_executionCts` field ownership
- 3 `[LoggerMessage]` partial declarations (lines 484-491) — source generator needs declaration in main
- 4 sibling type declarations after class close (lines 494-543): `ScriptResult` record, `ScriptErrorType` enum, `ScriptOutputLine` record, `ScriptOutputLevel` enum

**Crucial type-ownership reasoning** (per W9 D6 + W10 D5): sibling types stay with their only consumer's class declaration. `ScriptResult` is the RunAsync return type and ScriptErrorType is its error-categorization — both live in the file to keep the public surface cohesive. Same for `ScriptOutputLine` (the event-args type for `OutputReceived`).

### Flow A — ExecutionLifecycle (largest, tightly coupled) (~225 LoC)

**Methods**:
- `public async Task<ScriptResult> RunAsync(string, TimeSpan?, CancellationToken)` — line 111 — wire-up: Stop previous, create linked CTS, increment generation, schedule ExecuteScript on Task.Run, register timeout callback, await TCS.
- `public void Stop()` — line 161 — cancel CTS + interrupt engine + wait briefly for graceful shutdown.
- `private void InterruptEngine()` — line 179 — V8 interrupt with Volatile.Read + try/catch shutdown race.
- `private void ExecuteScript(string, TaskCompletionSource<ScriptResult>, long, CancellationToken)` — line 205 — ~155 LoC, the core execution logic. Includes: stale-task drop (generation re-check), engine publish via Interlocked.Exchange, ScriptConsole.CurrentEngine routing, main script Execute, onInit wrapper with v1.7.1 PATCH Item 2 failure flag, 4 catch blocks (ScriptInterruptedException / OperationCanceledException / ScriptEngineException w/ IsResourceLimit / fallback Exception), finally with onDispose wrapper + engine.Dispose() + CAS-protected _engine null.

**Depends on**:
- `_engine`, `_executionCts`, `_executionTask`, `_generation`, `_lock`, `_logger` (main fields)
- `ScriptConsole.CurrentEngine` static field (sibling file)
- `ScriptResult`, `ScriptErrorType`, `ScriptOutputLine` (sibling types)
- `LogScriptError`, `LogOnInitError`, `LogInterruptFailed` LoggerMessage partials (main)
- ClearScript V8 types: `V8ScriptEngine`, `ScriptInterruptedException`, `ScriptEngineException`

**Rationale for grouping**: Per W3 R3 sister-lesson + W12 UdsClient precedent — `RunAsync + InterruptEngine + ExecuteScript` share mutable `_engine` + `_generation` + `_executionCts` state across a tight execution lifecycle. Splitting these across partial files would scatter the lifecycle primitive across 3+ partials. The Dispose method also closes over `_executionCts`; keeping Dispose in main means the lifecycle cluster is co-located in Flow A but with cleanup at the disposal boundary. **D2**: Dispose stays in main with the field declarations (state-ownership), not in Flow A.

**File**: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine/ExecutionLifecycleFlow.cs`
**Required usings**: `Microsoft.ClearScript`, `Microsoft.ClearScript.V8`, `System.Threading.Tasks`, `System.Threading` (CancellationTokenSource) — verify during execution; many already in main.

### Flow B — CreateEngine (~90 LoC)

**Methods**:
- `private V8ScriptEngine CreateEngine(CancellationToken ct)` — line 372 — V8 engine creation with V8RuntimeConstraints (MaxNewSpaceSizeMB + MaxOldSpaceSizeMB) + MaxRuntimeHeapSize soft monitor + console object wiring (log/warn/error) + AddRestrictedHostObject<IScriptCanApi>("can") + AddRestrictedHostObject<IScriptDbcApi>("dbc") + utilities (log/warn/error/delay/hex/toHex).

**Depends on**:
- `_options`, `_canApi`, `_dbcApi`, `_utilities` (main fields)
- `ScriptConsole.CurrentEngine` static field (sibling file)
- ClearScript types: `V8RuntimeConstraints`, `V8ScriptEngine`, `V8ScriptEngineFlags`
- `IScriptCanApi`, `IScriptDbcApi` interfaces (sibling files)

**Rationale for grouping**: CreateEngine is the **only** method that touches ClearScript host-object wiring. It's a self-contained factory. Keeping it in one partial isolates the ClearScript-specific knowledge (V8ScriptEngine API surface + AddHostObject lambdas) from the execution lifecycle in Flow A.

**File**: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine/CreateEngineFlow.cs`
**Required usings**: `Microsoft.ClearScript`, `Microsoft.ClearScript.V8`

### Flow C — ScriptHelpers (~55 LoC)

**Methods**:
- `internal void EmitOutput(ScriptOutputLine)` — line 454 — raises `OutputReceived` event.
- `private static bool IsResourceLimit(ScriptEngineException)` — line 469 — v1.7.3 PATCH Item 1 heuristic discrimination of V8 resource-cap violations from generic runtime errors.

**Depends on**:
- `OutputReceived` event (main)
- `ScriptEngineException` (ClearScript type)

**Rationale for grouping**: Both are stateless helpers — `EmitOutput` is a one-line event-invoke; `IsResourceLimit` is a pure function on `ScriptEngineException.Message`. They're called only from ExecuteScript (Flow A). Keeping them together in a small helper-partial keeps Flow A focused on lifecycle.

**File**: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine/ScriptHelpersFlow.cs`
**Required usings**: minimal

## Architecture invariants (per W3-W13 patterns)

1. **Public API unchanged**: `RunAsync` + `Stop` + `Dispose` + `IsRunning` property + `OutputReceived` event + 2 ctors callable with identical signatures from `ScriptEngine`.
2. **partial-class visibility**: private methods + private fields visible across partial files. `ExecuteScript` (Flow A) calls `CreateEngine` (Flow B) + `EmitOutput` (Flow C) + `IsResourceLimit` (Flow C) via plain method invocations.
3. **State stays close to its owner**: All 12 fields stay in main (per W11 D5 sister-lesson). `Dispose` (main) reaches into `_executionCts` (main) directly; Flow A closures reach into `_executionCts`, `_engine`, `_generation` via partial-class visibility.
4. **LoggerMessage partials in main**: `[LoggerMessage]` source generator requires the attribute + partial declaration in the file where the method body is generated. The 3 partial declarations stay in main (their generation targets are reachable from Flow A + Flow C methods).
5. **Sibling types stay with class**: `ScriptResult`, `ScriptErrorType`, `ScriptOutputLine`, `ScriptOutputLevel` remain in main file after the class closes (per W9 D6 + W10 D5 sister).
6. **No new files outside the established directory**: `ScriptEngine/` is a sibling to `ScriptEngine.cs` (matches `UdsClient/` and `AscParser/` precedent).
7. **Instance class with IDisposable + thread-safety + generation counter + virtual resources (V8 native binding)**: New shape in the partial-pattern repertoire — sister of W12 UdsClient (no V8, no generation) but adds the **external native binding lifecycle** (V8ScriptEngine needs Dispose). The partial mechanism works identically; only the visibility of state ownership differs.

## Verification

- `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore`: 0 errors
- `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ScriptEngine"`: all tests pass without modification
- `dotnet test --no-restore --nologo -c Debug`: full solution builds clean, no regressions

## Risk notes

- **R1 (low)**: Missing `using` directives — per W3-W13 CONFIRMED lesson (19+ confirmations). Pre-scan method bodies. Likely new usings: `Microsoft.ClearScript` (already in main), `Microsoft.ClearScript.V8` (already in main), `System.Threading` (already in main). **3 main usings cover Flow A's needs**; Flow B may need a duplicate of `Microsoft.ClearScript.V8` for `V8RuntimeConstraints`/`V8ScriptEngineFlags`; Flow C probably no new usings.
- **R2 (low)**: Deletion script line-count assertion — per W8.5 PATCH D7 CONFIRMED. Apply `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. **W13 T1 2/3 lesson application**: use loose assertion to handle `wc -l` vs `splitlines(keepends=True)` off-by-one on un-trailing-newline files.
- **R3 (low)**: Execution lifecycle coupling — `RunAsync + InterruptEngine + ExecuteScript` must stay in **one partial** (Flow A). _Engine + _generation + _executionCts cross-reference between these 3 methods; splitting them across partials would scatter the lifecycle primitives. **Lesson captured as W14 D2**: Dispose + ctors + fields stay in main with field ownership; the 3 execution methods stay together in Flow A.
- **R4 (low)**: LoggerMessage partial declarations stay in main. The 3 partial declarations (`LogInterruptFailed`, `LogOnInitError`, `LogScriptError`) must not move — source generator requires declaration with the class scope. **Confirmed via W10 + W11 + W12 + W13 pattern**: LoggerMessage partials in main file, calls from any partial via visibility.
- **R5 (very low)**: V8 native binding lifecycle integrity — `engine.Dispose()` is called in ExecuteScript's finally block. Partial-class split does not change this lifecycle; the method-body verbatim extraction preserves the call sequence. _engine CAS-protected null-out at line 352 — synchronized with Volatile.Read in InterruptEngine (line 189). Both stay coupled via partial visibility. **Verified structurally**: Flow A keeps all 3 methods together, so the lifecycle stays intact.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W13 CONFIRMED direct partial-class visibility is sufficient.
- **No sub-class creation**: ScriptEngine stays a single class.
- **No test changes**: All existing tests stay unmodified.
- **No public/internal API surface change**.
- **No extraction of `ScriptResult`/`ScriptErrorType`/etc. to separate files** — these are sibling types tightly coupled to ScriptEngine's public surface; moving them would force consumers to add using directives.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A — `ExecutionLifecycleFlow` (~225 LoC, RunAsync + InterruptEngine + ExecuteScript). Largest, validates execution-lifecycle-coupling pattern.
2. **Task 2**: Extract Flow B — `CreateEngineFlow` (~90 LoC, V8 engine creation with sandboxed globals).
3. **Task 3**: Extract Flow C — `ScriptHelpersFlow` (~55 LoC, EmitOutput + IsResourceLimit).
4. **Task 4**: Bump version v3.28.0 → v3.29.0 + write release notes (MINOR ship commit).
5. **Task 5**: Tier-3 push + tag + GH release.

Total: 5 tasks, ~4 source commits (1 per flow + 1 ship).

## Decision log

- **D1**: 3 partials with descriptive names (`ExecutionLifecycleFlow` / `CreateEngineFlow` / `ScriptHelpersFlow`).
- **D2**: **ExecuteScript stays with RunAsync + InterruptEngine** in Flow A (not split) — sister-lesson of W3 R3 (mutable-state coupling). Dispose + fields + ctors stay in main with field ownership (sister-lesson of W11 D5 + W12 D5).
- **D3**: Branch name `feature/w14-script-engine-god-class`.
- **D4**: Order tasks: **A (largest, execution lifecycle) → B (CreateEngine) → C (Helpers)** — largest first validates the partial + V8 lifecycle coupling; B second (ClearScript wiring isolation); C last (smallest, stateless helpers).
- **D5**: 3 LoggerMessage partial declarations stay in main per W10 + W11 + W12 + W13 sister-lesson (source generator scope requirement).
- **D6**: 4 sibling types (ScriptResult record + ScriptErrorType enum + ScriptOutputLine record + ScriptOutputLevel enum) stay in main file after class close per W9 D6 + W10 D5 sister (type-declaration-with-class-ownership).
- **D7**: Plan LoC-trajectory-table formula explicitly applied per W8.5 PATCH CONFIRMED: `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`.
- **D8**: W11 R3 helper-extract-on-demand — foreknown NOT needed. `ExecuteScript` (~155 LoC) is the only candidate that exceeds 50 LoC. Its body has clear logical sections (entry-check → engine publish → main execute → onInit → catches → finally cleanup) but **splitting them into private helpers would require changing the method shape** (current is one continuous try/catch/finally). Verdict: keep the body inline; per W12 D7 sister-lesson (one-method-one-purpose = execute script).

## Closing milestone context

This is the **11th god-class refactor** in the project. ScriptEngine is the **5th App layer** god-class (W3 TraceViewerViewModel + W4 AppShellViewModel + W5 SignalViewModel + W6 SendViewModel + W7 MultiFrameSendViewModel + W8 TraceChartViewModel + W11 AppHostBuilder). ScriptEngine is the **1st App layer god-class with external native binding lifecycle** (V8ScriptEngine is an unmanaged resource that needs Dispose). Validates the partial-class split pattern works for: instance class with IDisposable + thread-safety + generation counter + external native resource lifecycle + LoggerMessage partials + sibling-record/enum types (sister of W12 UdsClient but with V8 binding).

If W14 ships + tests pass + lesson confirmations hold, W14.5 vault-only PATCH (lesson-promotion candidates: `execution-lifecycle-cluster-must-not-be-split-across-partials` + `v8-native-binding-lifecycle-survives-partial-extraction-verbatim` + W13 T1 wc-l 2/3 ready for promotion) and W15 (next candidate: `ReplayTimeline.cs` 469 LoC internal sealed partial) become natural next steps.
