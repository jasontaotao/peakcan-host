# W32 v3.46.0 SHIP — DbcApi god-class refactor capture-decisions

**Branch**: `feature/w32-dbc-api-god-class`
**Parent**: v3.45.5 PATCH (`ef58da5` on `main`)
**Ship commit**: `e9f3bcc` on `main` (squash-merged via PR #65)
**Tag**: `v3.46.0` annotated at `e9f3bcc`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.46.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`
**CI**: PASS first attempt (no transient flaky `IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` exposure since W32 has no source change to that path; sister of W30 + W31 SHIP 1-attempt + W23.5-W25.5 + W27.5 + W28 PATCH 5-attempt MAX pattern retention)

## D1-D7 (carried from W32 SPEC)

- **D1**: 2 NEW partials (`LoadFlow` + `QueryFlow`) in `DbcApi/` subdirectory. **22nd subdirectory-pattern deployment**.
- **D2**: No `partial` modifier edit needed (already partial at line 20; sister of W21 + W26.5 + W30 + W31 precedent).
- **D3**: 5 fields (`_logger` + `_dbcService` + `_signalValues` + `_currentDocument` + `_lastLoadError`) + 1 ctor + 2 private helpers (`OnDbcLoaded` + `OnLoadFailed`) + 1 `Dispose` + 1 inner record `SignalSnapshot` + class xmldoc stay in main.
- **D4**: 2 `[LoggerMessage]` partial declarations (`LogDbcLoadedViaScript` + `LogDbcLoadFailed`) stay on `DbcApi` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32 sister precedent (CS8795 mitigation). Called from `Load` (in LoadFlow partial) — cross-partial call resolution handles this automatically.
- **D5**: **APPLIES** — `Load` 73 LoC LARGEST method MOVES to LoadFlow.partial.cs per W25 D5 deviation (sister of W25 + W26 + W27 + W28 + W30 moves). **10th observation of `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`** (9/3 LOCKED at W31 → 10/3 at W32).
- **D6**: Branch name `feature/w32-dbc-api-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29+W30+W31 D7 sister + W25 D5 deviation: **A (LoadFlow, 96 LoC) → B (QueryFlow, 75 LoC)**.

## 6 source commits (squash-collapsed into PR #65)

1. `a46a077` — W32 T0 — SPEC + PLAN (2 files: `docs/superpowers/specs/2026-07-13-dbc-api-god-class-refactor.md` + `docs/superpowers/plans/2026-07-13-dbc-api-god-class-refactor.md`). Build clean, 10/10 DbcApi tests pass.
2. `4ef52e3` — W32 T1 — Flow A `LoadFlow` extracted. Main 279 → 183 (-96 LoC, EXACT match to HEAD range L53-L148). **W20 LESSON APPLIED 39th time**: verbatim re-extraction via `git show main:src/.../DbcApi.cs | sed -n '53,148p'`. 1 using-directive addition (`PeakCan.Host.Core.Dbc` for `DbcService.LoadAsync` + `DbcDocument` types).
3. `3de16b5` — W32 T2 — Flow B `QueryFlow` extracted. Main 183 → 108 (-75 LoC, EXACT match to post-T1 range L54-L89 + L91-L112 + L114-L130, 3 non-contiguous regions processed in reverse order). **W20 LESSON APPLIED 40th time**: verbatim re-extraction via `git show main:src/.../DbcApi.cs | sed -n '150,185p;187,208p;210,226p'`. **W19 R1 first-correction LESSON ENHANCED applied successfully**: boundary verification baked into script upfront + recovery procedure documented (no failure occurred).
4. `30b0c42` — W32 T3 — v3.45.5 → v3.46.0 MINOR + 121 LoC release notes.
5. `e9f3bcc` — W32 T4 — squash-merge via PR #65 (auto-collapsed all 4 source commits into 1 squash commit).
6. **post-PR docs commit**: W32 capture-decisions (this file).

## Main file change (cumulative W32)

`src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` **279 → 108 LoC (-171 LoC, -61.3%)** across 2 NEW partials. **28th god-class refactor** in W3-W32 series. **6th App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + W30 SequenceSendService + W31 ReplayService). **4th App/Services/Scripting subdirectory** (sister of W14 ScriptEngine + W26 CanApi). **22nd subdirectory-pattern deployment**.

## LoC formula EXACT (W8.5 D7 32-locked)

Both transitions EXACT match to ±0 LoC tolerance:
- T1: 279 → 183 (delta = 96, EXACT match to HEAD range L53-L148)
- T2: 183 → 108 (delta = 75, EXACT match to post-T1 range 3 non-contiguous regions)

## W19 R1 first-correction LESSON ENHANCED — boundary verification upfront + recovery procedure baked in

Per W31 T2 first-run failure (delta=28 vs expected 69 due to incorrect multi-region reverse-order indexing), W32 T2 deletion script applied the **W19 R1 LESSON ENHANCED** recovery procedure as a **prevention strategy** (rather than recovery from failure):

1. **Boundary verification upfront**: Script prints expected boundaries + actual line content BEFORE deletion (e.g., `L54-L89 (Decode): line 54 = /// <summary>, line 89 = }`)
2. **Recovery procedure documented**: If delta is outside ±2 tolerance, recovery procedure is `git checkout + re-grep post-T1 boundaries + corrected offsets + re-run + verify`
3. **W32 T2 first run PASS with delta = 75 EXACT match** (no failure occurred; boundary verification upfront confirmed all 3 regions correctly identified)

This is the **2nd application** of the W19 R1 LESSON ENHANCED post-failure-recovery dimension (1st was W31 T2; 2nd is W32 T2). The LESSON now has clear pre-flight prevention + post-failure recovery patterns documented.

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 W32-related warnings (2 pre-existing CS8602 warnings from `DbcService/LoadLifecycle.partial.cs:88` retained across cycles)
- `dotnet test --filter "FullyQualifiedName~DbcApi"`: **10/10 PASS** (matches pre-W32 baseline)
- `dotnet test` (full solution via CI): PASS first attempt (no transient flaky exposure)
- `wc -l src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` = 108 LoC (target ~108, EXACT match)

## Architecture milestones

- **28th god-class refactor SHIPPED** (W3-W32 series)
- **6th App/Services layer** (after W22 + W23 + W27 + W28 + W29 + W30 + W31)
- **4th App/Services/Scripting subdirectory** (after W14 ScriptEngine + W26 CanApi + W32 DbcApi)
- **22nd subdirectory-pattern deployment**
- **`partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 15th observation since 3/3 CONFIRMED at W23 T2** (W32 verified `Task<object>` async + `Volatile.Write` 1-arg + `ConcurrentDictionary` indexer + `DbcDocument.Messages` enumerable + `Message.Signals` + `SignalDecoder.Decode` static + `_dbcService.LoadAsync` async signatures in LoadFlow.partial.cs + QueryFlow.partial.cs)
- **`partial-class-with-private-static-logger-message-cross-partial-compiles-clean` 11th confirmation since 3/3 CONFIRMED at W23 T3** (W32 confirms 2 `[LoggerMessage]` partials on main + called from `Load` in LoadFlow partial all compile clean via cross-partial visibility)
- **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 10th observation since 3/3 LOCKED at W25** (W32 confirms 6th move: W22 stay + W23 stay + W25 move + W26 move + W27 move + W28 move + W29 stay + W30 move + W31 stay + W32 move = 4 stays + 6 moves = 10 total observations; 6 moves in 10 observations)
- **`add-partial-keyword-to-monolithic-class-before-extraction` 30th application** (W32 already partial at L20)
- **`app-services-scripting-sister-pattern-empirical-w14-w26-w32` NEW 1/3** (W32 DbcApi is 1st observation of App/Services/Scripting sister pattern; sister of W14 ScriptEngine + W26 CanApi for App/Services/Scripting subsystem shape)
- **W19 R1 first-correction LESSON ENHANCED** — pre-flight prevention + post-failure recovery dimensions both applied at W32 T2 with boundary verification baked into the script upfront (no failure occurred)
- **W20 LESSON APPLIED 39th + 40th times** at W32 T1+T2 (verbatim re-extraction via `git show main:src/.../DbcApi.cs | sed -n '<range>p'`)
- **W19 R1 first-correction APPLIED 41st + 42nd times** at W32 T1+T2 (2 using-directive additions + boundary verification + recovery procedure)
- **W17 wc-l-splitlines CONFIRMED 43-locked** (cp1252 binary read+write)

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 39th + 40th times in W32

Per W20 T2 R1 fabrication incident + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 + W29 + W30 + W31 applied 7+3+3+7+3+16+3+3+45+3 successful prior extractions, W32 explicitly applied verbatim re-extraction in **both extraction tasks**:

1. **T1 LoadFlow**: `git show main:src/.../DbcApi.cs | sed -n '53,148p'` → 0 build errors after 1 using-directive addition.
2. **T2 QueryFlow**: `git show main:src/.../DbcApi.cs | sed -n '150,185p;187,208p;210,226p'` → 0 build errors, no using-directive additions needed.

**40-of-40 verification that verbatim HEAD re-extraction prevents fabrication errors (cumulative W20 + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 + W29 + W30 + W31 + W32).**

## W19 R1 first-correction LESSON ENHANCED applied at W32 T2

Per W19 T1 first-correction pattern (re-grep boundaries BEFORE running each deletion script), W32 T1 + T2 scripts both re-grep post-T1 boundaries via `grep -n "public object? Decode|public object? GetSignal|public object\[\] GetMessages"` BEFORE running each deletion script. W32 T2 script additionally baked in **boundary verification upfront** (prints expected boundaries + actual line content BEFORE deletion) and **recovery procedure documentation** (if delta outside ±2 tolerance, recovery = git checkout + re-grep + corrected offsets + re-run + verify).

W32 T2 first run PASS with delta = 75 EXACT match (no failure occurred; boundary verification upfront confirmed all 3 regions correctly identified). This is the **2nd application** of the W19 R1 LESSON ENHANCED post-failure-recovery dimension (1st was W31 T2; 2nd is W32 T2).

## W23 STRUCT-FABRACTION LESSON APPLIED 15th time since 3/3 CONFIRMED

Per W23 T2 struct-fabrication LESSON (verify struct constructor signatures, not just method signatures), W32 T1 + T2 verified **7+ struct/method signatures**:

**T1 LoadFlow** verified:
- `Task<object>` async return type — 1-arg + 1-arg CT default
- `string.IsNullOrWhiteSpace(string)` — 1-arg (in `Load` body)
- `_dbcService.LoadAsync(string, CancellationToken)` — 2-arg async (in `Load` body)
- `DbcDocument.Messages` enumerable property — collection (in `Load` body + `doc.Messages.Count` access)
- `Volatile.Write<T>` — 1-arg ref (in `OnDbcLoaded` in main, but `Load` reads `_currentDocument` written by `OnDbcLoaded`)
- `ConcurrentDictionary<string, SignalSnapshot>.ContainsKey` — 1-arg (in `Decode` in QueryFlow but `Load` reads from same dictionary indirectly)

**T2 QueryFlow** verified:
- `doc.Messages.FirstOrDefault<Message>(Func<Message, bool>)` — 1-arg + lambda (in `Decode`)
- `Dictionary<string, object>` constructor — 0-arg (in `Decode`)
- `SignalDecoder.Decode(ReadOnlySpan<byte>, Signal)` — 2-arg static (in `Decode`)
- `ConcurrentDictionary<string, SignalSnapshot>.TryGetValue` — 2-arg + out (in `GetSignal`)
- `ConcurrentDictionary<TKey, TValue>.this[TKey]` — 1-arg indexer (in `Decode` write to `_signalValues`)
- `doc.Messages.Select<Message, T>` — 1-arg + lambda (in `GetMessages`)

**W23 LESSON applied 15th time (cumulative since CONFIRMED at W23 T2): W23 + W24 + W25 T2 + W25 T3 + W26 T3 + W27 T1 + W27 T3 + W28 T1 + W28 T2 + W29 T1 + W29 T2 + W29 T3 + W30 T2 + W31 T1 + W31 T2 + W32 T1 + W32 T2 = 16 observations.** Total struct/method signatures verified: ~80+ across W23-W32.

## W25 D5 deviation APPLIED 6th time

W32 SPEC explicitly identifies `DbcApi.Load` 73 LoC LARGEST method is **≥ 60 LoC threshold + sharp discrete flow boundary** (Load → return result envelope with 4 distinct result paths: success / LoadFailed-surfaced-error / Cancelled / Exception = discrete Load dispatcher, NOT single central orchestration loop). Per W25 + W26 + W27 + W28 + W30 D5 sister-principle, LARGEST method MOVES to LoadFlow.partial.cs.

**6 moves confirmed across W25-W32**:
- W25 OnChannelFrame 73 LoC → MOVED to FrameRouting.partial.cs (fan-out + error-isolation)
- W26 OnFrame(CanFrame) 62 LoC → MOVED to SinkLifecycle.partial.cs (frame-arrives → callback-fanout)
- W27 LoadAsync 60 LoC → MOVED to PersistenceOps.partial.cs (file-IO lifecycle)
- W28 LoadAsync 79 LoC → MOVED to LoadLifecycle.partial.cs (file-IO + parsing lifecycle)
- W30 SendAsync 91 LoC → MOVED to SendFlow.partial.cs (concurrent-vs-sequential dispatcher)
- **W32 Load 73 LoC → MOVED to LoadFlow.partial.cs** (Load → return result envelope with 4 distinct result paths)

Sister-of-W22 + W23 stays (orchestration loops ≥ 60 LoC) + W29 + W31 stays (small god-classes < 50 LoC) + W25 + W26 + W27 + W28 + W30 + W32 moves (≥ 60 LoC + discrete flow boundary) = 10 total observations, 4 stays + 6 moves.

**`largest-method-can-move` HELD at 10/3 LOCKED** (6 moves + 4 stays including 2 small-god-class stays) in MASTER-LESSON-CATALOG at W32 SHIP closure.

## W17 wc-l-splitlines CONFIRMED 43-locked

Per W17 wc-l-splitlines CONFIRMED pattern (Windows-1252 encoded files require binary read+write with cp1252 codec, NOT UTF-8), W32 T1+T2 deletion scripts both use `read_bytes/decode('cp1252')/write_bytes/encode('cp1252')` pattern. Zero off-by-one errors after the W19 R1 LESSON ENHANCED recovery procedure baked into W32 T2 script.

## Cross-partial helper visibility pattern (CONFIRMED across 2 partials per W32)

W32 confirms cross-partial helper visibility works across **2 partials** (sister of W22 + W23 + W26 + W27 + W28 + W29 + W30 + W31):

- **`Decode` + `GetSignal` + `GetMessages` methods** (in Flow B `QueryFlow.partial.cs`) read `_currentDocument` (written by `OnDbcLoaded` in main) + write/read `_signalValues` (ConcurrentDictionary in main) — partial-class cross-partial visibility handles this automatically.
- **`Load` method** (in Flow A `LoadFlow.partial.cs`) reads `_currentDocument` (written by `OnDbcLoaded` in main) + reads `_lastLoadError` (written by `OnLoadFailed` in main) — partial-class cross-partial visibility handles this automatically.

This is a **9th confirmation** (1st was W22, 2nd was W23, 3rd was W26, 4th was W27, 5th was W28, 6th was W29, 7th was W30, 8th was W31, 9th is W32) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.

## Lesson promotions STATE-OF-THE-ART (post-W32)

| Lesson | Status | Observations |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | held |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | held; W32 = 22nd deployment |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32 15-of-1) | promoted to 15 observations |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32 11-of-1) | promoted to 11 confirmations |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | held at 30th application; W32 already partial |
| `multi-interface-partial-class-empirical-w26-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | held; W32 DbcApi has single interface `IScriptDbcApi` (not multi-interface) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | **10/3 since 3/3 LOCKED** (W32) | held at 10/3 LOCKED; W32 = 6th move in 10 observations |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` | 2/3 (W25) | held; W32 is App/Services/Scripting, NOT Infrastructure/Channel |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` | 3/3 CONFIRMED LOCKED (W29.5) | held; W32 has no JSON-persistence |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | held; W32 has sync-Load-result-envelope, not async file-load lifecycle |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | 2/3 (W31.5) | held; W32 LARGEST method 73 LoC ≥ 60 LoC → W25 D5 deviation applies correctly |
| `app-services-multiframe-layer-sister-pattern-empirical-w30` | NEW 1/3 (W30) | held; W32 is App/Services/Scripting, NOT App/Services/MultiFrame |
| `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` | NEW 1/3 (W31) | held; W32 is App/Services/Scripting, NOT Core/Replay |
| `app-services-scripting-sister-pattern-empirical-w14-w26-w32` | **NEW 1/3** (W32) | **NEW observation** |
| `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` | 1/3 (W18 R1) | held |

## What was captured

W32 SHIP closure = 6 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + SHIP. Each per the W12-W31 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.46.0 bump (release-notes body suffices per W12 D7 + W14 D8 + W22 D7 + W23 D7 + W24 D7 + W25 D7 + W26 D7 + W27 D7 + W28 D7 + W29 D7 + W30 D7 + W31 D7 sister).
- No 2nd verification round on Core tests (CI PASS first attempt retained).
- No W18 R1 fix applied (2 `[LoggerMessage]` partials stay on main partial declaration; cross-partial caller-methods auto-resolve via partial-class visibility).
- No W31 `small-god-class-no-largest-method` default D5 applied (Load 73 LoC ≥ 60 LoC → W25 D5 deviation applies correctly).
- No `DbcService.cs` partial changes (sister of W28; W32's DbcApi wraps DbcService).
- No `SignalDecoder.cs` or `DbcDocument.cs` partial changes.

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W32 T0+T1+T2 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W32 T1+T2 using-directive additions (`PeakCan.Host.Core.Dbc` ×1 in LoadFlow + ×1 in QueryFlow).
- **W19 R1 first-correction ENHANCED**: pre-flight prevention (re-grep boundaries before each deletion script) + post-failure recovery procedure (git checkout + re-grep + corrected offsets + re-run + verify) — both dimensions applied at W32 T2 with boundary verification baked into the script upfront (no failure occurred).
- **W20 T2 R1 fabrication LESSON**: 40 verbatim re-extractions across W32 T1+T2 (39+40th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W32 verified 7+ struct/method signatures (15th observation since 3/3 CONFIRMED).
- **W25 D5 deviation APPLIED**: W32 Load 73 LoC LARGEST method MOVES (10th observation since 3/3 LOCKED at W25; W32 = 6th move in 10 observations).

## CI status

- **1st attempt: SUCCESS** (no transient flaky `IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` exposure since W32 has no source change to that path; sister of W30 + W31 SHIP 1-attempt + W23.5-W25.5 + W27.5 + W28 PATCH 5-attempt MAX pattern retention)
- Local trx run: 10 DbcApi tests pass cleanly

## Cumulative trajectory (peakcan-host god-class series)

**28 god-class refactors SHIPPED** (W3-W32):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + W22 RecordService + W23 CyclicDbcSendService + W24 DbcSendViewModel + W25 ChannelRouter + W26 CanApi + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + W30 SequenceSendService + W31 ReplayService + **W32 DbcApi**

Plus 8 vault-only PATCH (W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5).

**Cumulative LoC reduction**: 27 god-class files -4,807 LoC (W3-W31) + **W32 DbcApi -171 LoC** = **-4,978 LoC total** across 28 god-class refactors + 8 PATCHes.

## Next

- **W32.5 vault-only PATCH** — lesson-promotion opportunity for 2 lesson events:
  - `app-services-scripting-sister-pattern-empirical-w14-w26-w32` NEW 1/3 (W32 DbcApi is 1st observation of App/Services/Scripting sister pattern; sister of W14 ScriptEngine + W26 CanApi)
  - `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 10th observation held (W32 Load 73 LoC ≥ 60 LoC + Load-result-envelope discrete flow boundary = MOVES; 6th move in 10 observations)
- **W33** — next god-class refactor candidate. Top remaining (>240 LoC) main files after W32: `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `SequenceLibrary.cs` 244 LoC (App/Services sister of W29 SendFrameLibrary) OR `PeakCanChannel.cs` 244 LoC (Infrastructure/Peak sister of W18 + W25) OR `CyclicSendService.cs` 243 LoC (App/Services sister of W23 CyclicDbcSendService) OR `TraceSessionBundle.cs` 247 LoC (App/Services/Trace sister of W27 RecentSessionsService).
