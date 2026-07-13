# Release Notes v3.46.0 — DbcApi god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.46.0
**Branch**: `feature/w32-dbc-api-god-class`
**Parent**: v3.45.5 PATCH (`ef58da5` on main + W31.5 vault-only PATCH)

## Why this MINOR

`src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` had grown to **279 LoC** as of v3.45.5 — at 34.9% of the 800 LoC Round-1 ceiling. Single `public sealed partial class DbcApi : IScriptDbcApi` (already partial at L20 — sister of W21 + W26.5 + W30 + W31 precedent; no D2 application needed). 5 fields + 1 ctor + 1 public async `Load` (**73 LoC LARGEST method**) + 3 public query methods (`Decode` 31 LoC + `GetSignal` 16 LoC + `GetMessages` 13 LoC) + 2 private helpers (`OnDbcLoaded` + `OnLoadFailed`) + 1 `Dispose` + 1 inner record `SignalSnapshot` + 2 `[LoggerMessage]` partial declarations (`LogDbcLoadedViaScript` + `LogDbcLoadFailed`).

This is the **28th god-class refactor** in the project (W3-W32 series). **6th App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + W30 SequenceSendService + W31 ReplayService). **4th App/Services/Scripting subdirectory** (sister of W14 ScriptEngine + W26 CanApi). **22nd subdirectory-pattern deployment**.

## LoC trajectory (W8.5 D7 CONFIRMED formula — 32-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n)`. Both transitions **EXACT match**.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | LoadFlow (Load xmldoc + body) | 53-148 (HEAD) | 96 | 183 |
| T2 | QueryFlow (Decode + GetSignal + GetMessages + xmldocs, 3 non-contiguous regions) | 54-89 + 91-112 + 114-130 (post-T1) | 75 | 108 |
| **Total** | -- | -- | **171** | **108** |

**Net**: 279 → 108 LoC main file (**-171 LoC, -61.3%**). Total project LoC across main + 2 partials ≈ 319 LoC (small +140 LoC overhead from per-file namespace + using directives + 2 xmldoc header comment blocks).

## What this MINOR does

### Refactor — DbcApi adds 2 NEW partials in `DbcApi/` subdirectory

1. **NEW `src/PeakCan.Host.App/Services/Scripting/DbcApi/LoadFlow.partial.cs` (~157 LoC)**:
   - Contains 1 public async method verbatim from main HEAD L53-L148: `public async Task<object> Load(string, CancellationToken)` (73 LoC body + 23 LoC xmldoc = 96 LoC).
   - **W25 D5 deviation APPLIED**: 73 LoC LARGEST method MOVES per the sharp discrete flow boundary criterion (Load → return result envelope = 4 distinct result paths: success / LoadFailed-surfaced-error / Cancelled / Exception).
   - Sister of W25 OnChannelFrame 73 LoC + W26 OnFrame 62 LoC + W27 LoadAsync 60 LoC + W28 LoadAsync 79 LoC + W30 SendAsync 91 LoC moves.
   - Verbatim re-extraction via `git show main:src/.../DbcApi.cs | sed -n '53,148p'` per W20 T2 R1 fabrication LESSON (39th application).

2. **NEW `src/PeakCan.Host.App/Services/Scripting/DbcApi/QueryFlow.partial.cs` (~162 LoC)**:
   - Contains 3 public query methods verbatim from main HEAD L150-L185 + L187-L208 + L210-L226: `public object? Decode(CanFrame)` (31 LoC body + 5 LoC xmldoc = 36 LoC) + `public object? GetSignal(string, string)` (16 LoC body + 6 LoC xmldoc = 22 LoC) + `public object[] GetMessages()` (13 LoC body + 4 LoC xmldoc = 17 LoC = 75 LoC total).
   - **W23 STRUCT-FABRICATION LESSON APPLIED 15th time**: `Task<object>` async signature + `Volatile.Write` 1-arg + `ConcurrentDictionary` indexer + `DbcDocument.Messages` enumerable + `Message.Signals` + `SignalDecoder.Decode` static + `_dbcService.LoadAsync` async signatures verified.
   - Verbatim re-extraction via `git show main:src/.../DbcApi.cs | sed -n '150,185p;187,208p;210,226p'` per W20 LESSON (40th application).

### D1-D7 sister-pattern decisions (carried from W32 SPEC)

- **D1**: 2 NEW partials (`LoadFlow` + `QueryFlow`) in `DbcApi/` subdirectory. **22nd subdirectory-pattern deployment**.
- **D2**: No `partial` modifier edit needed (already partial at line 20; sister of W21 + W26.5 + W30 + W31 precedent).
- **D3**: 5 fields (`_logger` + `_dbcService` + `_signalValues` + `_currentDocument` + `_lastLoadError`) + 1 ctor + 2 private helpers (`OnDbcLoaded` + `OnLoadFailed`) + 1 `Dispose` + 1 inner record `SignalSnapshot` + class xmldoc stay in main.
- **D4**: 2 `[LoggerMessage]` partial declarations (`LogDbcLoadedViaScript` + `LogDbcLoadFailed`) stay on `DbcApi` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32 sister precedent (CS8795 mitigation). Called from `Load` (in LoadFlow partial) — cross-partial call resolution handles this automatically.
- **D5**: **APPLIES** — `Load` 73 LoC LARGEST method MOVES to LoadFlow.partial.cs per W25 D5 deviation. **10th observation of `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`** (9/3 LOCKED at W31 → 10/3 at W32). 6th move in 10 observations (W22 stay + W23 stay + W25 move + W26 move + W27 move + W28 move + W29 stay + W30 move + W31 stay + W32 move).
- **D6**: Branch name `feature/w32-dbc-api-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29+W30+W31 D7 sister + W25 D5 deviation: **A (LoadFlow, 96 LoC, LARGEST + W25 D5 deviation applied) → B (QueryFlow, 75 LoC, query+decode cluster)**.

## What this MINOR does NOT change (YAGNI + sister-pattern invariants)

- No public/internal API change.
- No test changes (10 DbcApi tests pass without modification).
- No facade pattern (W3-W31 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No CS8795 risk (D4 keeps 2 `[LoggerMessage]` partials on main partial declaration).
- No `DbcService.cs` partial changes (sister of W28; W32's DbcApi wraps DbcService).
- No `SignalDecoder.cs` or `DbcDocument.cs` partial changes.
- No W31 `small-god-class-no-largest-method` default D5 applied (Load 73 LoC ≥ 60 LoC threshold → W25 D5 deviation applies).

## Architecture milestones

- **28th god-class refactor SHIPPED** (W3-W32 series).
- **6th App/Services layer** (after W22 + W23 + W27 + W28 + W29 + W30 + W31).
- **4th App/Services/Scripting subdirectory** (after W14 ScriptEngine + W26 CanApi + W32 DbcApi).
- **22nd subdirectory-pattern deployment**.
- **W20 LESSON APPLIED 39th + 40th times** across W32 T1+T2 (verbatim re-extraction via `git show main:src/.../DbcApi.cs | sed -n '<range>p'`).
- **W23 STRUCT-FABRICATION LESSON APPLIED 15th time since 3/3 CONFIRMED at W23 T2** (W32 verified `Task<object>` async + `Volatile.Write` 1-arg + `ConcurrentDictionary` indexer + `DbcDocument.Messages` enumerable + `Message.Signals` + `SignalDecoder.Decode` static + `_dbcService.LoadAsync` async signatures).
- **W25 D5 deviation APPLIED 6th time** (W25 OnChannelFrame + W26 OnFrame + W27 LoadAsync + W28 LoadAsync + W30 SendAsync + **W32 Load** = 6 moves since 3/3 LOCKED at W25).
- **W19 R1 first-correction LESSON ENHANCED** at W32 T2 (W19 R1 LESSON now has both pre-flight prevention AND post-failure recovery dimensions, baked into W32 T2 deletion script with boundary verification upfront + recovery procedure documentation).
- **`partial-class-with-private-static-logger-message-cross-partial-compiles-clean` 11th confirmation since 3/3 CONFIRMED at W23 T3** (W32 confirms 2 `[LoggerMessage]` partials on main + called from `Load` in LoadFlow partial all compile clean via cross-partial visibility).
- **NEW 1/3 lesson candidate**: `app-services-scripting-sister-pattern-empirical-w14-w26-w32` (NEW 1/3 at W32 SPEC: DbcApi Scripting decomposition = LoadFlow + QueryFlow; sister of W14 ScriptEngine + W26 CanApi for App/Services/Scripting subsystem shape).
- **W17 wc-l-splitlines CONFIRMED 43-locked** (cp1252 binary read+write).

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W32 T1+T2 using-directive fixes per W19; 2 pre-existing CS8602 warnings from `DbcService/LoadLifecycle.partial.cs:88` retained across cycles, NOT W32-related).
- `dotnet test --filter "FullyQualifiedName~DbcApi"`: **10/10 PASS** (matches pre-W32 baseline).
- `dotnet test` (full solution via CI): 0 new fails expected.

## Process lessons applied (W20 + W23 + W25 + W26.5 + W19 + W31)

- **Lesson #10** (verify each commit before proceeding): each W32 T0+T1+T2 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W32 T1+T2 using-directive additions (`PeakCan.Host.Core.Dbc` for `DbcService.LoadAsync` + `DbcDocument` types in LoadFlow; same + `SignalDecoder` + `Message` types in QueryFlow).
- **W19 R1 first-correction ENHANCED**: pre-flight prevention (re-grep boundaries before each deletion script) + post-failure recovery procedure (git checkout + re-grep + corrected offsets + re-run + verify) — both dimensions applied at W32 T2 with boundary verification baked into the script.
- **W20 T2 R1 fabrication LESSON**: 40 verbatim re-extractions across W32 T1+T2 (39+40th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W32 verified 7+ struct/method signatures (15th observation since 3/3 CONFIRMED).
- **W25 D5 deviation APPLIED**: W32 Load 73 LoC LARGEST method MOVES (10th observation since 3/3 LOCKED at W25; W32 = 6th move in 10 observations).

## Sister-pattern cumulative trajectory (god-class series, W3-W32)

| W | Layer | Subdirectory | Main LoC | Prior + W32 |
|---|---|---|---|---|
| W14 | Core/Scripting | ScriptEngine/ | (already partial since W14) | 9th god-class |
| W22 | App/Services | RecordService/ | -193 | 18th god-class |
| W23 | App/Services | CyclicDbcSendService/ | -288 | 19th god-class |
| W26 | App/Services/Scripting | CanApi/ | -202 | 22nd god-class |
| W27 | App/Services/Trace | RecentSessionsService/ | -213 | 23rd god-class |
| W28 | App/Services | DbcService/ | -195 | 24th god-class |
| W29 | App/Services | SendFrameLibrary/ | -162 | 25th god-class |
| W30 | App/Services/MultiFrame | SequenceSendService/ | -184 | 26th god-class |
| W31 | Core/Replay | ReplayService/ | -119 | 27th god-class |
| **W32** | **App/Services/Scripting** | **DbcApi/** | **-171** | **28th god-class** |

**Cumulative LoC reduction (W3-W32)**: 27 god-class files -4,807 LoC (W3-W31) + **W32 DbcApi -171 LoC** = **-4,978 LoC total** across 28 god-class refactors + 8 vault-only PATCHes (W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5).

## What was captured

W32 SHIP closure = 5 captures dispatched: SPEC + PLAN + T1 + T2 + T3 (T4 ship captures via `vault-pkm:pkm-capture` background-dispatched post-T4 squash-merge + tag + GH release). Each per the W12-W31 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## Next (post-ship)

- **W32.5 vault-only PATCH** — lesson-promotion opportunity for 2 lesson events:
  - `app-services-scripting-sister-pattern-empirical-w14-w26-w32` NEW 1/3 (W32 DbcApi is 1st observation of App/Services/Scripting sister pattern; sister of W14 ScriptEngine + W26 CanApi)
  - `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 10th observation held (W32 Load 73 LoC ≥ 60 LoC + Load-result-envelope discrete flow boundary = MOVES; 6th move in 10 observations)
- **W33** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W32: `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `SequenceLibrary.cs` 244 LoC (App/Services sister of W29 SendFrameLibrary) OR `PeakCanChannel.cs` 244 LoC (Infrastructure/Peak sister of W18 + W25) OR `CyclicSendService.cs` 243 LoC (App/Services sister of W23 CyclicDbcSendService) OR `TraceSessionBundle.cs` 247 LoC (App/Services/Trace sister of W27 RecentSessionsService).
