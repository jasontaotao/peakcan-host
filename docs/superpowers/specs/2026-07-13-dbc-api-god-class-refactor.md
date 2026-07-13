# W32 SPEC — DbcApi god-class refactor (28th overall, 4th App/Services/Scripting)

**Date**: 2026-07-13
**Target class**: `src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` (279 LoC)
**Target version**: v3.46.0 MINOR
**Sister pattern**: W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + W30 SequenceSendService + W31 ReplayService (7 App/Services god-class refactors). **28th god-class refactor** in W3-W31 series.

## Context

`DbcApi` (279 LoC) is the **4th App/Services/Scripting** god-class candidate in the W3-W31 series (27 refactors shipped, 3 prior App/Services/Scripting sisters: W14 ScriptEngine + W26 CanApi). The class exposes DBC decoding operations to the JavaScript scripting engine (ClearScript V8 binder).

**Class shape** (already verified via direct read):

- `public sealed partial class DbcApi : IScriptDbcApi` (L20) — **already partial** (W21 + W26.5 3/3 CONFIRMED + W30 + W31 sister precedent; no D2 needed)
- 5 fields: `_logger` + `_dbcService` + `_signalValues` (ConcurrentDictionary) + `_currentDocument` + `_lastLoadError` (L22-L34)
- 1 ctor (L36-L51, ~16 LoC) — subscribes to `_dbcService.DbcLoaded` + `_dbcService.LoadFailed` events
- 1 public async method `Load(string path, CancellationToken ct = default)` returning `Task<object>` — **73 LoC LARGEST method** (L76-L148)
- 1 public method `Decode(CanFrame)` returning `object?` (L155-L185, 31 LoC)
- 1 public method `GetSignal(string, string)` returning `object?` (L193-L208, 16 LoC)
- 1 public method `GetMessages()` returning `object[]` (L214-L226, 13 LoC)
- 2 private helpers: `OnDbcLoaded(DbcDocument)` (L228-L239, 12 LoC) + `OnLoadFailed(Error)` (L241-L249, 9 LoC)
- 1 public method `Dispose()` (L251-L263, 13 LoC)
- 1 inner record `SignalSnapshot(double, double, string?, DateTimeOffset)` (L265-L272)
- 2 `[LoggerMessage]` partial declarations: `LogDbcLoadedViaScript` (L274-L275) + `LogDbcLoadFailed` (L277-L278) — **STAY ON MAIN per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31 sister precedent (CS8795 mitigation)**

**LARGEST method analysis** per W25 D5 deviation:

- `Load` 73 LoC ≥ **60 LoC threshold** ✓
- Sharp discrete flow boundary: **Load → return result envelope** (success / LoadFailed-surfaced-error / Cancelled / Exception = 4 distinct result paths in single method)
- **NOT a single central orchestration loop** (clearly discrete Load dispatcher)
- **W25 D5 deviation APPLIES** — LARGEST method MOVES to LoadFlow.partial.cs (sister of W25 OnChannelFrame 73 LoC + W26 OnFrame 62 LoC + W27 LoadAsync 60 LoC + W28 LoadAsync 79 LoC + W30 SendAsync 91 LoC moves)

**Sister-extraction sequence** (App/Services/Scripting subsystem):

- W14 ScriptEngine (already partial: `ScriptEngine/ExecutionLifecycleFlow.cs` 274 LoC)
- W26 CanApi (3 partials: SinkLifecycle + CallbackRegistry + SendAndQuery)
- **W32 DbcApi (2 partials: LoadFlow + QueryFlow)** — different decomposition shape (Load+state vs Query+decode)

## W32 D1-D7

- **D1**: 2 NEW partials (`LoadFlow` + `QueryFlow`) in `DbcApi/` subdirectory. **22nd subdirectory-pattern deployment**.
- **D2**: No `partial` modifier edit needed (already partial at line 20; W21 + W26.5 + W30 + W31 sister precedent).
- **D3**: 5 fields (`_logger` + `_dbcService` + `_signalValues` + `_currentDocument` + `_lastLoadError`) + 1 ctor + 2 private helpers (`OnDbcLoaded` + `OnLoadFailed`) + 1 `Dispose` + 1 inner record `SignalSnapshot` + 2 `[LoggerMessage]` partials + class xmldoc stay in main.
- **D4**: 2 `[LoggerMessage]` partial declarations (`LogDbcLoadedViaScript` + `LogDbcLoadFailed`) stay on `DbcApi` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31 sister precedent (CS8795 mitigation). Called from `Load` (in LoadFlow partial) — cross-partial call resolution handles this automatically.
- **D5**: **APPLIES** — `Load` 73 LoC LARGEST method MOVES to LoadFlow.partial.cs per W25 D5 deviation (sister of W25 + W26 + W27 + W28 + W30 moves). **10th observation of `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`** (9/3 LOCKED at W31 → 10/3 at W32).
- **D6**: Branch name `feature/w32-dbc-api-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29+W30+W31 D7 sister + W25 D5 deviation: **A (LoadFlow, 96 LoC, LARGEST + W25 D5 deviation applied) → B (QueryFlow, 75 LoC, query+decode cluster)**.

## Architecture

Sister pattern of W22 + W23 + W27 + W28 + W29 + W30 + W31 (subdirectory + non-suffix `.partial.cs` filenames). 28th god-class refactor. **6th App/Services layer** + **4th App/Services/Scripting subdirectory** + **22nd subdirectory-pattern deployment** + **6th multi-interface partial-class extraction** (sister of W26 CanApi `IFrameSink + IScriptCanApi` + W31 ReplayService `IReplayService + IDisposable`).

### Flow boundaries (Phase 1 verified)

**Stays in main (~108 LoC)**:
- `using` block (L1-L6) + namespace (L8) + class xmldoc (L10-L19)
- `public sealed partial class DbcApi : IScriptDbcApi` (L20)
- 5 fields: `_logger` + `_dbcService` + `_signalValues` + `_currentDocument` + `_lastLoadError` (L22-L34)
- 1 ctor (L36-L51)
- 2 private helpers: `OnDbcLoaded(DbcDocument)` (L228-L239) + `OnLoadFailed(Error)` (L241-L249)
- 1 `Dispose()` (L251-L263)
- 1 inner record `SignalSnapshot(double, double, string?, DateTimeOffset)` (L265-L272)
- 2 `[LoggerMessage]` partial declarations (L274-L278)

**Flow A — LoadFlow (~96 LoC, T1) → `DbcApi/LoadFlow.partial.cs`**:

- 1 public async method `Load(string path, CancellationToken ct = default)` returning `Task<object>` (L53-L148, **xmldoc L53-L75 + body L76-L148 = 96 LoC, LARGEST method moves per W25 D5 deviation**)

Touches: `_dbcService.LoadAsync` + `_currentDocument` + `_lastLoadError` + 2 `[LoggerMessage]` partials (`LogDbcLoadedViaScript` + `LogDbcLoadFailed`).

**Flow B — QueryFlow (~75 LoC, T2) → `DbcApi/QueryFlow.partial.cs`**:

- 1 public method `Decode(CanFrame)` returning `object?` (L150-L185, xmldoc L150-L154 + body L155-L185 = 36 LoC)
- 1 public method `GetSignal(string, string)` returning `object?` (L187-L208, xmldoc L187-L192 + body L193-L208 = 22 LoC)
- 1 public method `GetMessages()` returning `object[]` (L210-L226, xmldoc L210-L213 + body L214-L226 = 17 LoC)

Touches: `_currentDocument` (read-only) + `_signalValues` (write in `Decode`).

**Cross-partial caller pattern**: `Decode` (in QueryFlow partial) reads `_currentDocument` (Volatile.Write'd in `OnDbcLoaded` in main) + writes `_signalValues` (ConcurrentDictionary in main). `Load` (in LoadFlow partial) reads `_currentDocument` + reads `_lastLoadError`. Both partials read the same fields — partial-class cross-partial visibility handles this automatically (sister of W22+W23+W24+W25+W26+W27+W28+W29+W30+W31 cross-partial helper pattern).

## LoC trajectory (W8.5 D7 32-locked + W19 R1 first-correction ENHANCED + W17 wc-l-splitlines CONFIRMED + W20 fabrication LESSON APPLIED 38+ times in W31 + W23 STRUCT-FABRACTION LESSON APPLIED 14 times)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — LoadFlow | L53-L148 (xmldoc + Load body = 96 LoC) | 96 | 1 | 183 |
| T2 | B — QueryFlow | L150-L185 (Decode) + L187-L208 (GetSignal) + L210-L226 (GetMessages) = 75 LoC, processed in reverse order | 75 | 1 | 108 |
| T3 | v3.45.5 -> v3.46.0 | (no source) | 0 | 0 | 108 |
| T4 | ship | -- | -- | -- | 108 |

Cumulative: 279 -> 183 -> 108 main. **Re-grep + range verify after each task per W19 R1 ENHANCED (pre-flight prevention + post-failure recovery)**.

## W20 + W23 LESSON APPLIED — verbatim re-extract + struct-ctor verification

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle struct-constructor fabrication, W32 will:

1. **Re-grep boundaries BEFORE running each deletion script** (Phase 1 done above; re-verify before T1 + T2 script runs).
2. **Re-extract original code from main HEAD via `git show main:src/.../DbcApi.cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **CRITICAL: Verify `Task<object>` async signature + anonymous-object return type + `Volatile.Write` 1-arg signature + `ConcurrentDictionary` indexer signature** — W23 LESSON applied (sister of W31 `Task.Run` + `EmitFrameToSinkAsync` verification).
4. **Verify `DbcDocument.Messages` enumerable signature + `Message.Signals` signature + `SignalDecoder.Decode` static signature + `_dbcService.LoadAsync` async signature** — W23 LESSON applied (new signature verifications).
5. **Build + run filter tests after each task** to catch any extraction errors immediately.

## Tasks

### T0: Branch + SPEC + PLAN commits

```bash
git checkout -b feature/w32-dbc-api-god-class main
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DbcApi" --logger "console;verbosity=minimal"
git add docs/superpowers/specs/2026-07-13-dbc-api-god-class-refactor.md
git commit -m "W32 spec: DbcApi god-class refactor (2 partials + 5-task roll-out, 28th overall, 6th App/Services, 4th App/Services/Scripting, 22nd subdirectory-pattern deployment)"
git add docs/superpowers/plans/2026-07-13-dbc-api-god-class-refactor.md
git commit -m "W32 plan: DbcApi god-class refactor (2 partials: LoadFlow + QueryFlow)"
```

### T1: LoadFlow partial (~96 LoC)

Write `scripts/w32_task1_delete_loadflow.py` with W19 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern. Range: L53-L148 (xmldoc + Load body = 96 LoC). Expected: 279 - 96 = 183 LoC. Build + tests, commit.

### T2: QueryFlow partial (~75 LoC)

Re-grep post-T1 ranges. Write `scripts/w32_task2_delete_queryflow.py`. Range: 3 non-contiguous regions (Decode L150-L185 + GetSignal L187-L208 + GetMessages L210-L226 = 75 LoC total), processed in reverse order per W19 R1 ENHANCED. Expected: 183 - 75 = 108 LoC. Build + tests, commit.

### T3: v3.45.5 → v3.46.0 MINOR + release notes

Mirror W31 release notes format. MINOR (2 NEW partial extractions = architectural change).

### T4: Tier-3 ship

`gh pr create` → `--squash --delete-branch` → `git tag v3.46.0` → `gh release create`.

## Sister-lesson candidates to monitor

| Lesson | Status | What W32 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W32 7th god-class application (T1+T2) — 39th application total |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31 14-of-1) | W32 15th observation (`Task<object>` async + `Volatile.Write` 1-arg + `ConcurrentDictionary` indexer + `DbcDocument.Messages` enumerable signatures verified) |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31 10-of-1) | W32 11th confirmation (2 `[LoggerMessage]` partials on main + called from `Load` in LoadFlow partial) |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | W32 already partial (30th cumulative confirmation) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | 9/3 since 3/3 LOCKED (W31) | W32 10th observation (Load 73 LoC ≥ 60 LoC + Load-result-envelope discrete flow boundary = MOVES; 6th move in 10 observations) |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | W32 22nd deployment, sister-of-W31 |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` | 3/3 CONFIRMED LOCKED (W29.5) | N/A — W32 has no JSON-persistence |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | N/A — W32 `Load` is sync-result-envelope, not async file-load lifecycle |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | 1/3 → 2/3 (W31.5) | N/A — W32 LARGEST method 73 LoC ≥ 60 LoC → W25 D5 deviation applies |
| `app-services-multiframe-layer-sister-pattern-empirical-w30` | NEW 1/3 (W30) | N/A — W32 is App/Services/Scripting, NOT App/Services/MultiFrame |
| `multi-interface-partial-class-empirical-w26-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | N/A — W32 DbcApi has single interface `IScriptDbcApi` (not multi-interface) |
| `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` | NEW 1/3 (W31) | N/A — W32 is App/Services/Scripting, NOT Core/Replay |
| `app-services-scripting-sister-pattern-empirical-w14-w26-w32` | **NEW W32 1/3** | W32 1st observation: DbcApi Scripting decomposition (LoadFlow + QueryFlow) = 2-partial pattern for DBC-decoding operations; sister of W14 ScriptEngine (different decomposition shape — W14 was ScriptEngine flow-cluster, W26 was multi-interface callback-registry, W32 is Load+Query) |

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~DbcApi"`: tests pass without modification
- `dotnet test` (full solution): 0 new fails
- `wc -l src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` ≤ 120 LoC (target ~108)
- 2 NEW partial files in `DbcApi/` directory
- 5 fields + 1 ctor + 2 private helpers + 1 `Dispose` + 1 inner record + 2 `[LoggerMessage]` partials remain in main
- DI registration unchanged (production DI binds `AddSingleton<IScriptDbcApi, DbcApi>(...)`)
- Public API unchanged (`IScriptDbcApi` interface)
- Tag v3.46.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes.
- No facade pattern (W3-W31 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No W18 R1 mitigation needed (D4 keeps all 2 `[LoggerMessage]` partials on main partial declaration; CS8795 mitigation via cross-partial visibility).
- No `DbcService.cs` partial changes (sister of W28; W32's DbcApi wraps DbcService).
- No `SignalDecoder.cs` or `DbcDocument.cs` partial changes.
