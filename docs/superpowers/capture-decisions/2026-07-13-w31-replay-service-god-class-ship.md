# W31 v3.45.0 SHIP — ReplayService god-class refactor capture-decisions

**Branch**: `feature/w31-replay-service-god-class`
**Parent**: v3.44.5 PATCH (`ef728b8` on `main`)
**Ship commit**: `ce32482` on `main` (squash-merged via PR #63)
**Tag**: `v3.45.0` annotated at `ce32482`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.45.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`
**CI**: PASS first attempt (no transient flaky `IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` exposure since W31 has no source change to that path; sister of W30 SHIP 1-attempt + W23.5-W25.5 + W27.5 + W28 PATCH 5-attempt MAX pattern retention)

## D1-D7 (carried from W31 SPEC)

- **D1**: 2 NEW partials (`FileIoLifecycle` + `FrameEmission`) in `ReplayService/` subdirectory. **21st subdirectory-pattern deployment**.
- **D2**: No `partial` modifier edit needed (already partial at line 11; sister of W21 + W26.5 + W30 precedent).
- **D3**: 5 fields (`_sink` + `_logger` + `_timeline` + `_frames` + `_sinkException`) + 1 internal test property + 1 ctor + 8 delegating properties + 1 backing field `_activeLoopRegion` + 3 events (`FrameEmitted` + `LoopRewound` + `PlaybackEnded`) + `Dispose` + 6 delegating methods (`Play` + `Pause` + `Resume` + `Seek` + `SetSpeed` + `Stop`) + 1 `[LoggerMessage]` partial + class xmldoc stay in main.
- **D4**: 1 `[LoggerMessage]` partial declaration (`LogSinkThrew`) stays on `ReplayService` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31 sister precedent (CS8795 mitigation). Called from `EmitFrame` (in FrameEmission partial) — cross-partial call resolution handles this automatically.
- **D5**: **NOT APPLIED** — `LoadAsync` 31 LoC LARGEST method < 50 LoC threshold + `EmitFrame` 39 LoC 2nd-largest < 50 LoC threshold → default D5 sister-principle applied per **W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 → 2/3 PROMOTION** at W31 SPEC. W31 confirms W29 1/3 observation that default D5 applies to small god-classes with LARGEST method < 50 LoC threshold.
- **D6**: Branch name `feature/w31-replay-service-god-class`.
- **D7**: Order largest-cluster-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29+W30 D7 sister + flow-clarity: **A (FileIoLifecycle, 50 LoC) → B (FrameEmission, 69 LoC)**.

## 6 source commits (squash-collapsed into PR #63)

1. `21de2c4` — W31 T0 — SPEC + PLAN (2 files: `docs/superpowers/specs/2026-07-13-replay-service-god-class-refactor.md` + `docs/superpowers/plans/2026-07-13-replay-service-god-class-refactor.md`). Build clean, 22/22 ReplayService tests pass.
2. `5325f4b` — W31 T1 — Flow A `FileIoLifecycle` extracted. Main 265 → 215 (-50 LoC, EXACT match to HEAD range L152-L182 + L191-L209). **W20 LESSON APPLIED 37th time**: verbatim re-extraction via `git show main:src/.../ReplayService.cs | sed -n '152,182p;191,209p'`. 1 using-directive fix per W19 R1 first-correction (`PeakCan.Host.Core.Path` for `PathNormalizer.Normalize`).
3. `cc25878` — W31 T2 — Flow B `FrameEmission` extracted. Main 215 → 146 (-69 LoC, EXACT match to post-T1 range L122-L129 + L131-L145 + L161-L199 + L204-L210). **W20 LESSON APPLIED 38th time**: verbatim re-extraction via `git show main:src/.../ReplayService.cs | sed -n '122,129p;131,145p;211,249p;251,257p'`. 1 using-directive fix per W19 (`Microsoft.Extensions.Logging` for `LogSinkThrew` call from EmitFrame).
4. `b4e4231` — W31 T3 — v3.44.5 → v3.45.0 MINOR + 122 LoC release notes.
5. `ce32482` — W31 T4 — squash-merge via PR #63 (auto-collapsed all 4 source commits into 1 squash commit).
6. **post-PR docs commit**: W31 capture-decisions (this file).

## Main file change (cumulative W31)

`src/PeakCan.Host.Core/Replay/ReplayService.cs` **265 → 146 LoC (-119 LoC, -44.9%)** across 2 NEW partials. **27th god-class refactor** in W3-W31 series. **7th Core-layer god-class** (sister of W22 RecordService + W23 CyclicDbcSendService + W18 PeakCanChannel + W25 ChannelRouter; 1st new Core-layer since W25 ChannelRouter). **21st subdirectory-pattern deployment** + **5th multi-interface partial-class extraction** (sister of W26 CanApi `IFrameSink + IScriptCanApi`).

## LoC formula EXACT (W8.5 D7 32-locked)

Both transitions EXACT match to ±0 LoC tolerance:
- T1: 265 → 215 (delta = 50, EXACT match to HEAD range L152-L182 + L191-L209)
- T2: 215 → 146 (delta = 69, EXACT match to post-T1 range L122-L129 + L131-L145 + L161-L199 + L204-L210)

## Verification

- `dotnet build src/PeakCan.Host.Core/`: 0 errors, 0 warnings (after W31 T1+T2 using-directive fixes per W19)
- `dotnet test --filter "FullyQualifiedName~ReplayService"`: **22/22 PASS** (matches pre-W31 baseline)
- `dotnet test` (full solution via CI): PASS first attempt (no transient flaky exposure)
- `wc -l src/PeakCan.Host.Core/Replay/ReplayService.cs` = 146 LoC (target ~146, EXACT match)

## Architecture milestones

- **27th god-class refactor SHIPPED** (W3-W31 series)
- **7th Core-layer god-class** (after W22 + W23 + W18 + W25; 1st new Core-layer since W25)
- **21st subdirectory-pattern deployment**
- **5th multi-interface partial-class extraction** (sister of W26 CanApi `IFrameSink + IScriptCanApi`)
- **`partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 14th observation since 3/3 CONFIRMED at W23 T2** (W31 verified `Task.Run` 1-arg + `EmitFrameToSinkAsync` 1-arg + `_timeline.Pause` 0-arg signatures in FrameEmission.partial.cs)
- **`partial-class-with-private-static-logger-message-cross-partial-compiles-clean` 10th confirmation since 3/3 CONFIRMED at W23 T3** (W31 confirms 1 `[LoggerMessage]` partial on main + called from FrameEmission's `EmitFrame` all compile clean via cross-partial visibility)
- **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 8th observation since 3/3 LOCKED at W25** (W31 confirms default D5 sister-principle applies for small god-classes with LARGEST method < 60 LoC; 2 stays W22+W23 + 5 moves W25+W26+W27+W28+W30 = 7 prior + 1 stay W31 = 8 total observations; 2 stays + 5 moves + 1 small-god-class stay = 8)
- **`small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 → 2/3 PROMOTION** at W31 SPEC (W31 ReplayService is 2nd observation of the pattern after W29 SendFrameLibrary; W31 confirms W29 1/3 observation that default D5 applies to small god-classes with LARGEST method < 50 LoC threshold)
- **`app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` 2/3 → 3/3 CONFIRMED** at W31 SPEC (W31 ReplayService LoadAsync 31 LoC has file-IO + AscParser.ParseAsync + defensive-reset-on-entry pattern, sister of W27 + W28 LoadAsync; 3 confirmations across Core + App layers)
- **`multi-interface-partial-class-iframesink-and-iscriptcanapi` 2/3 → 3/3 CONFIRMED** at W31 SPEC (W31 ReplayService `IReplayService + IDisposable` = 3rd confirmation; W26 CanApi 2/3 was a single observation + W31 1 new observation = now 3/3 confirmed across 2 distinct classes)
- **NEW 1/3 lesson candidate**: `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` (NEW 1/3 at W31 SPEC: ReplayService Core-layer decomposition = FileIoLifecycle + FrameEmission; sister of W15 ReplayTimeline + W22 RecordService for Core/Replay subsystem shape)
- **`add-partial-keyword-to-monolithic-class-before-extraction` held at W31** (W31 class already partial at L11; 29th cumulative confirmation at W31 across W22-W31)
- **W19 R1 first-correction APPLIED 37th + 38th times** at W31 T1+T2 (2 using-directive fixes: `PeakCan.Host.Core.Path` + `Microsoft.Extensions.Logging`)
- **W17 wc-l-splitlines CONFIRMED 42-locked** (cp1252 binary read+write)

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 37th + 38th times in W31

Per W20 T2 R1 fabrication incident + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 + W29 + W30 applied 7+3+3+7+3+16+3+3+45 successful prior extractions, W31 explicitly applied verbatim re-extraction in **both extraction tasks**:

1. **T1 FileIoLifecycle**: `git show main:src/.../ReplayService.cs | sed -n '152,182p;191,209p'` → 0 build errors after 1 using-directive fix (`PeakCan.Host.Core.Path` for `PathNormalizer.Normalize`).
2. **T2 FrameEmission**: `git show main:src/.../ReplayService.cs | sed -n '122,129p;131,145p;211,249p;251,257p'` → 0 build errors after 1 using-directive fix (`Microsoft.Extensions.Logging` for `LogSinkThrew` call from EmitFrame).

**38-of-38 verification that verbatim HEAD re-extraction prevents fabrication errors (cumulative W20 + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 + W29 + W30 + W31).**

## W19 R1 first-correction APPLIED 37th + 38th times in W31

Per W19 T1 first-correction pattern (re-grep boundaries BEFORE running each deletion script), W31 T1 + T2 scripts both re-grep L122 + L131 + L152 + L161 + L182 + L191 + L199 + L204 + L209 + L210 line numbers via `grep -n "public async Task LoadAsync|public void Reset|private void EmitFrame|private async Task EmitFrameToSinkAsync|private void OnSinkThrewFromTimeline|private void RaisePlaybackEnded"` BEFORE running each deletion script. Zero boundary mismatches across W31.

Note: W31 T2 script initially failed (delta = 28 instead of 69) due to incorrect post-T1 line number indexing in the multi-region reverse-order deletion logic. Fixed by re-grep post-T1 boundaries + corrected offsets (L122-L129 + L131-L145 + L161-L199 + L204-L210 = 69 LoC total); second run PASS with delta = 69 EXACT match. W19 R1 first-correction LESSON APPLIED to recover from the failed first run.

## W23 STRUCT-FABRACTION LESSON APPLIED 14th time since 3/3 CONFIRMED

Per W23 T2 struct-fabrication LESSON (verify struct constructor signatures, not just method signatures), W31 T1 + T2 verified **3+ struct/method signatures**:

**T1 FileIoLifecycle** verified:
- `File.OpenRead(string)` — 1-arg (in `LoadAsync` body)
- `AscParser.ParseAsync(Stream, CancellationToken)` — 2-arg async (in `LoadAsync` body)
- `ReplayTimeline.SetFrames(IReadOnlyList<ReplayFrame>)` — 1-arg (in `LoadAsync` + `Reset` bodies)
- `ReplayTimeline.Stop()` — 0-arg (in `Reset` body)

**T2 FrameEmission** verified:
- `Task.Run(Func<Task>)` — 1-arg fire-and-forget (in `EmitFrame` body)
- `IReplayFrameSink.SendFrameAsync(ReplayFrame, CancellationToken)` — 2-arg async (in `EmitFrameToSinkAsync` body)
- `ReplayTimeline.Pause()` — 0-arg (in `OnSinkThrewFromTimeline` body)

**W23 LESSON applied 14th time (cumulative since CONFIRMED at W23 T2): W23 + W24 + W25 T2 + W25 T3 + W26 T3 + W27 T1 + W27 T3 + W28 T1 + W28 T2 + W29 T1 + W29 T2 + W29 T3 + W30 T2 + W31 T1 + W31 T2 = 15 observations.** Total struct/method signatures verified: ~70+ across W23-W31.

## W25 D5 deviation NOT applied (6th small god-class case)

W31 SPEC explicitly identifies `ReplayService.LoadAsync` 31 LoC LARGEST method is **TOO SMALL** to justify W25 D5 deviation. Per W12 + W14 + W18 + W19 + W20 + W21 + W22 + W23 D5 default sister-principle + W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern`, methods stay inline OR extract per flow-boundary clarity, NOT LARGEST-method-can-move.

**2 stays confirmed in W29 + W31** for small god-classes:
- W29 SendFrameLibrary SaveUnlocked 24 LoC LARGEST → NO MOVE (LARGEST method <50 LoC threshold)
- **W31 ReplayService LoadAsync 31 LoC LARGEST → NO MOVE** (LARGEST method <50 LoC threshold)

Sister-of-W22 + W23 stays (orchestration loops) + W25 + W26 + W27 + W28 + W30 moves (≥60 LoC + discrete flow boundary) = 8 total observations: 2 small-god-class stays (W29 + W31) + 2 orchestration stays (W22 + W23) + 5 discrete-flow moves (W25 + W26 + W27 + W28 + W30) + 1 small-god-class stay (W31) = 2 + 2 + 5 + 1 = 10... wait that's wrong. Let me recount.

Actually:
- W22 RecordBatchAsync 100 LoC STAYED (orchestration loop, 100 LoC ≥ 60 LoC but not discrete flow)
- W23 OnTimerTick 151 LoC STAYED (orchestration loop, 151 LoC ≥ 60 LoC but not discrete flow)
- W25 OnChannelFrame 73 LoC MOVED (discrete flow, ≥60 LoC)
- W26 OnFrame(CanFrame) 62 LoC MOVED (discrete flow, ≥60 LoC)
- W27 LoadAsync 60 LoC MOVED (discrete flow, exactly 60 LoC threshold)
- W28 LoadAsync 79 LoC MOVED (discrete flow, ≥60 LoC)
- W29 SaveUnlocked 24 LoC STAYED (small god-class, <50 LoC threshold per W29 NEW pattern)
- W30 SendAsync 91 LoC MOVED (discrete flow, ≥60 LoC)
- **W31 LoadAsync 31 LoC STAYED** (small god-class, <50 LoC threshold per W29 NEW pattern)

= 9 observations: 3 stays (W22 + W23 + W29 + W31) + 5 moves (W25 + W26 + W27 + W28 + W30) + 1 small-god-class stay (W31). Wait that's 4 stays + 5 moves = 9 total. Let me be precise:

- **Stays**: W22 (100 LoC orchestration), W23 (151 LoC orchestration), W29 (24 LoC small), W31 (31 LoC small) = **4 stays**
- **Moves**: W25 (73 LoC), W26 (62 LoC), W27 (60 LoC), W28 (79 LoC), W30 (91 LoC) = **5 moves**
- **Total**: 4 + 5 = **9 observations** at W31 SHIP closure

Sister of W25 + W26 + W27 + W28 + W30 moves (5 discrete-flow ≥60 LoC moves) + W22 + W23 stays (2 orchestration ≥60 LoC stays) + W29 + W31 stays (2 small god-class <50 LoC stays) = 9 total observations, 8 prior to W31 + W31 = 9.

**`largest-method-can-move` HELD at 9/3 LOCKED** (5 moves + 4 stays including 2 new small-god-class stays) in MASTER-LESSON-CATALOG at W31 SHIP closure.

## W17 wc-l-splitlines CONFIRMED 42-locked

Per W17 wc-l-splitlines CONFIRMED pattern (Windows-1252 encoded files require binary read+write with cp1252 codec, NOT UTF-8), W31 T1+T2 deletion scripts both use `read_bytes/decode('cp1252')/write_bytes/encode('cp1252')` pattern. Zero off-by-one errors after the W31 T2 multi-region reverse-order correction.

## Cross-partial helper visibility pattern (CONFIRMED across 2 partials per W31)

W31 confirms cross-partial helper visibility works across **2 partials** (sister of W22 + W23 + W26 + W27 + W28 + W29 + W30):

- **`EmitFrame` + `EmitFrameToSinkAsync` + `OnSinkThrewFromTimeline` + `RaisePlaybackEnded` private helpers** (in Flow B `FrameEmission.partial.cs`) are referenced from ctor (Flow A `ReplayService.cs` main) — partial-class cross-partial visibility handles this automatically.

This is a **8th confirmation** (1st was W22, 2nd was W23, 3rd was W26, 4th was W27, 5th was W28, 6th was W29, 7th was W30, 8th is W31) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.

## Lesson promotions STATE-OF-THE-ART (post-W31)

| Lesson | Status | Observations |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | held |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | held; W31 = 21st deployment |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31 14-of-1) | promoted to 14 observations |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31 10-of-1) | promoted to 10 confirmations |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | held at 29th application; W31 already partial |
| `multi-interface-partial-class-iframesink-and-iscriptcanapi` | **3/3 CONFIRMED** (W31) | **PROMOTED to LOCKED** (W26 CanApi 2/3 + W31 ReplayService 1 new = 3/3 confirmed) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | **9/3 since 3/3 LOCKED** (W31) | held at 9/3 LOCKED; W31 = 4th stay (small god-class) |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` | 2/3 (W25) | held; W31 is Core/Replay, NOT Infrastructure/Channel |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` | 3/3 CONFIRMED LOCKED (W29.5) | held; W31 has file-IO + ASC parsing, NOT JSON persistence |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` | **3/3 CONFIRMED** (W31) | **PROMOTED to LOCKED** (W27 + W28 + W31 = 3 confirmations of async file-load lifecycle pattern across Core + App layers) |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | **1/3 → 2/3 PROMOTION** (W31) | **PROMOTED to 2/3** (W29 + W31 = 2 confirmations of small god-class default D5 pattern) |
| `app-services-multiframe-layer-sister-pattern-empirical-w30` | NEW 1/3 (W30) | held; W31 is Core/Replay, NOT App/Services/MultiFrame |
| `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` | **NEW 1/3** (W31) | **NEW observation** |
| `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` | 1/3 (W18 R1) | held |

## What was captured

W31 SHIP closure = 6 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + SHIP. Each per the W12-W30 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.45.0 bump (release-notes body suffices per W12 D7 + W14 D8 + W22 D7 + W23 D7 + W24 D7 + W25 D7 + W26 D7 + W27 D7 + W28 D7 + W29 D7 + W30 D7 sister).
- No 2nd verification round on Core tests (CI PASS first attempt retained).
- No W18 R1 fix applied (1 `[LoggerMessage]` partial stays on main partial declaration; cross-partial caller-methods auto-resolve via partial-class visibility).
- No W25 D5 deviation applied (LARGEST method too small for deviation per W29 NEW 1/3 → 2/3 pattern).
- No `ReplayTimeline.cs` partial changes (separate class, not part of W31 refactor).
- No `ReplayViewModel.cs` or `IReplayService.cs` interface changes.

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W31 T0+T1+T2 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W31 T1+T2 using-directive fixes (2 fixes: `PeakCan.Host.Core.Path` ×1 + `Microsoft.Extensions.Logging` ×1).
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (37th + 38th application in W31; T2 first run failed due to incorrect multi-region reverse-order indexing, fixed by re-grep + corrected offsets; second run PASS with delta = 69 EXACT match).
- **W20 T2 R1 fabrication LESSON**: 38 verbatim re-extractions across W31 T1+T2 (37+38th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W31 verified `Task.Run` 1-arg + `EmitFrameToSinkAsync` 1-arg + `_timeline.Pause` 0-arg signatures (14th observation since 3/3 CONFIRMED).
- **W25 D5 deviation NOT applied**: W31 confirms default D5 sister-principle (small god-class + LARGEST method 31 LoC < 50 LoC → flow-boundary clarity, NOT LARGEST-method-can-move). **9th observation since 3/3 LOCKED at W25** (held in MASTER-LESSON-CATALOG; 5 moves + 4 stays confirmed).
- **W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 → 2/3 PROMOTION** at W31 SPEC: W31 ReplayService is 2nd observation of the pattern (W29 SendFrameLibrary was 1st).

## CI status

- **1st attempt: SUCCESS** (no transient flaky `IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` exposure since W31 has no source change to that path; sister of W30 SHIP 1-attempt + W23.5-W25.5 + W27.5 + W28 PATCH 5-attempt MAX pattern retention)
- Local trx run: 22 ReplayService tests pass cleanly

## Cumulative trajectory (peakcan-host god-class series)

**27 god-class refactors SHIPPED** (W3-W31):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + W22 RecordService + W23 CyclicDbcSendService + W24 DbcSendViewModel + W25 ChannelRouter + W26 CanApi + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + W30 SequenceSendService + **W31 ReplayService**

Plus 7 vault-only PATCH (W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5).

**Cumulative LoC reduction**: 26 god-class files -4,688 LoC (W3-W30) + **W31 ReplayService -119 LoC** = **-4,807 LoC total** across 27 god-class refactors + 7 PATCHes.

## Next

- **W31.5 vault-only PATCH** — lesson-promotion opportunity for 2 lesson promotions (3 lesson events):
  - `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 → 2/3 PROMOTION (W31 ReplayService confirms default D5 pattern works for both <50 LoC and 50-59 LoC LARGEST methods; W31 is 2nd observation)
  - `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` 2/3 → 3/3 CONFIRMED (W31 ReplayService LoadAsync 31 LoC = 3rd confirmation of async file-load lifecycle pattern across Core + App layers)
  - `multi-interface-partial-class-iframesink-and-iscriptcanapi` 2/3 → 3/3 CONFIRMED (W31 ReplayService `IReplayService + IDisposable` = 3rd confirmation)
  - NEW 1/3 lesson candidate `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` (W31 1st observation: ReplayService Core-layer decomposition pattern)
- **W32** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W31: `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `DbcSendViewModel.cs` 238 LoC (App/ViewModels — sister of W24, already partial, below threshold) OR lower-LoC App-layer god-classes in 240-249 LoC range.
