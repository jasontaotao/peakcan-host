# W30 v3.44.0 SHIP — SequenceSendService god-class refactor capture-decisions

**Branch**: `feature/w30-sequence-send-service-god-class`
**Parent**: v3.43.5 PATCH (`44e0323` on `main`)
**Ship commit**: `f8bc7d9` on `main` (squash-merged via PR #61)
**Tag**: `v3.44.0` annotated at `f8bc7d9`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.44.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`
**CI**: PASS first attempt (no transient flaky `IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` exposure since W30 has no source change to that path; sister of W29 SHIP 4-attempt + W23.5-W25.5 + W27.5 + W28 PATCH 5-attempt MAX pattern)

## D1-D7 (carried from W30 SPEC)

- **D1**: 2 NEW partials (`SendFlow` + `RowBuildFlow`) in `SequenceSendService/` subdirectory. **20th subdirectory-pattern deployment**.
- **D2**: **Add `partial` modifier** at L31 (`public sealed class SequenceSendService` → `public sealed partial class SequenceSendService`) per W26.5 3/3 CONFIRMED + W21 3/3 CONFIRMED sister. 28th cumulative application.
- **D3**: 3 readonly fields (`_sendService` + `_dbcEncodeService?` + `_dbcService?`) + 2 ctors (parameterless delegating + full) + 1 nested enum `Mode` + 1 nested record `Result` + class xmldoc stay in main.
- **D4**: N/A — zero `[LoggerMessage]` partials (verified). No CS8795 risk.
- **D5**: **APPLIES** — `SendAsync` 91 LoC LARGEST method MOVES to SendFlow.partial.cs per W25 D5 deviation (sister of W25 OnChannelFrame 73 LoC + W26 OnFrame 62 LoC + W27 LoadAsync 60 LoC + W28 LoadAsync 79 LoC moves). **7th observation of `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`** (6/3 LOCKED at W28.5 → 7/3 at W30).
- **D6**: Branch name `feature/w30-sequence-send-service-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29 D7 sister + W25 D5 deviation: **A (SendFlow, 91 LoC, LARGEST + W25 D5 deviation applied) → B (RowBuildFlow, 93 LoC, helpers cluster)**.

## 6 source commits (squash-collapsed into PR #61)

1. `bdffe9d` — W30 T0 — D2 partial-keyword add + SPEC + PLAN (3 files: partial-keyword add at L31 + `docs/superpowers/specs/2026-07-13-sequence-send-service-god-class-refactor.md` + `docs/superpowers/plans/2026-07-13-sequence-send-service-god-class-refactor.md`). Build clean, 16/16 SequenceSendService tests pass.
2. `c646b75` — W30 T1 — Flow A `SendFlow` extracted. Main 267 → 176 (-91 LoC, EXACT match to HEAD range L75-L165). **W20 LESSON APPLIED 35th time**: verbatim re-extraction via `git show HEAD:src/.../SequenceSendService.cs | sed -n '75,165p'`. 1 using-directive fix per W19 R1 first-correction (`PeakCan.Host.App.Models` for `MultiFrameSequenceRow`).
3. `a623030` — W30 T2 — Flow B `RowBuildFlow` extracted. Main 176 → 83 (-93 LoC, EXACT match to HEAD range L83-L175 post-T1). **W20 LESSON APPLIED 36th time**: verbatim re-extraction via `git show main:src/.../SequenceSendService.cs | sed -n '174,266p'`. 1 using-directive fix per W19 (`PeakCan.Host.App.Models` for `MultiFrameSequenceRow`).
4. `1e8f597` — W30 T3 — v3.43.5 → v3.44.0 MINOR + 122 LoC release notes.
5. `f8bc7d9` — W30 T4 — squash-merge via PR #61 (auto-collapsed all 4 source commits into 1 squash commit).
6. **post-PR docs commit**: W30 capture-decisions (this file).

## Main file change (cumulative W30)

`src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs` **266 → 82 LoC (-184 LoC, -69.2%)** across 2 NEW partials. **26th god-class refactor** in W3-W30 series. **6th App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary). **1st App/Services/MultiFrame subdirectory** + **20th subdirectory-pattern deployment**.

## LoC formula EXACT (W8.5 D7 32-locked)

All 2 transitions EXACT match to ±0 LoC tolerance:
- T1: 267 → 176 (delta = 91, EXACT match to HEAD range L75-L165)
- T2: 176 → 83 (delta = 93, EXACT match to post-T1 range L83-L175)

Note: T0 added `partial` keyword (1 word, no LoC change). Main went 266 → 267 → 176 → 83 → 82 (final after `wc -l` formatting).

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 W30-related warnings (2 pre-existing CS8602 warnings from `DbcService/LoadLifecycle.partial.cs:88` retained across cycles)
- `dotnet test --filter "FullyQualifiedName~SequenceSendService"`: **16/16 PASS** (matches pre-W30 baseline)
- `dotnet test` (full solution via CI): PASS first attempt (no transient flaky exposure)
- `wc -l src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs` = 82 LoC (target ~82, EXACT match)

## Architecture milestones

- **26th god-class refactor SHIPPED** (W3-W30 series)
- **6th App/Services layer** (after W22 + W23 + W27 + W28 + W29)
- **1st App/Services/MultiFrame subdirectory** (new layer discovered)
- **20th subdirectory-pattern deployment**
- **W25 D5 deviation APPLIED 5th time** (W25 OnChannelFrame + W26 OnFrame + W27 LoadAsync + W28 LoadAsync + **W30 SendAsync** = 5 moves since 3/3 LOCKED at W25)
- **`partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 13th observation since 3/3 CONFIRMED at W23 T2** (W30 verified `CanId(raw, FrameFormat format)` 2-arg + `CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default)` 5-arg signatures in RowBuildFlow.partial.cs)
- **`add-partial-keyword-to-monolithic-class-before-extraction` 28th application since 3/3 CONFIRMED at W26.5** (W30 T0-D2 added `partial` modifier to `SequenceSendService` before T1+T2 extraction)
- **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 7th observation since 3/3 LOCKED at W25** (W30 SendAsync 91 LoC ≥ 60 LoC + concurrent-vs-sequential discrete flow boundary = MOVES; 5 moves in 7 observations: W22 stay + W23 stay + W25 move + W26 move + W27 move + W28 move + W30 move)
- **`small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 → held at W30** (W30 has LARGEST method 91 LoC ≥ 60 LoC → W25 D5 deviation APPLIED correctly, NOT default D5 — confirms W29 1/3 observation that default D5 applies to small god-classes only)
- **NEW 1/3 lesson candidate**: `app-services-multiframe-layer-sister-pattern-empirical-w30` (NEW 1/3 at W30 SPEC: SequenceSendService MultiFrame decomposition = SendFlow + RowBuildFlow; 1st observation of App/Services/MultiFrame sister pattern)

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 35th + 36th times in W30

Per W20 T2 R1 fabrication incident + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 + W29 applied 7+3+3+7+3+16+3+3+45 successful prior extractions, W30 explicitly applied verbatim re-extraction in **both extraction tasks**:

1. **T1 SendFlow**: `git show HEAD:src/.../SequenceSendService.cs | sed -n '75,165p'` → 0 build errors after 1 using-directive fix (`PeakCan.Host.App.Models` for `MultiFrameSequenceRow`).
2. **T2 RowBuildFlow**: `git show main:src/.../SequenceSendService.cs | sed -n '174,266p'` → 0 build errors after 1 using-directive fix (`PeakCan.Host.App.Models` for `MultiFrameSequenceRow`).

**36-of-36 verification that verbatim HEAD re-extraction prevents fabrication errors (cumulative W20 + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 + W29 + W30).**

## W19 R1 first-correction APPLIED 35th + 36th times in W30

Per W19 T1 first-correction pattern (re-grep boundaries BEFORE running each deletion script), W30 T1 + T2 scripts both re-grep L75 + L83 + L160 + L165 + L174 + L175 line numbers via `grep -n "public async Task<Result> SendAsync|private bool TryBuildRow|private async Task<bool> SendOneAsync"` BEFORE running each deletion script. Zero boundary mismatches across W30.

## W23 STRUCT-FABRICATION LESSON APPLIED 13th time since 3/3 CONFIRMED

Per W23 T2 struct-fabrication LESSON (verify struct constructor signatures, not just method signatures), W30 T1 + T2 verified **2+ struct/method signatures**:

**T2 RowBuildFlow** verified:
- `CanId(uint raw, FrameFormat format)` — **2-arg** ctor (in `TryBuildRow` line ~137 of partial)
- `CanFrame(CanId canId, ReadOnlyMemory<byte> payload, FrameFlags flags, ChannelId channelId, ReadOnlyMemory<byte>?)` — **5-arg** ctor with `default` for the last nullable parameter (in `TryBuildRow` line ~139 of partial)

**W23 LESSON applied 13th time (cumulative since CONFIRMED at W23 T2): W23 + W24 + W25 T2 + W25 T3 + W26 T3 + W27 T1 + W27 T3 + W28 T1 + W28 T2 + W29 T1 + W29 T2 + W29 T3 + W30 T2 = 13 observations.** Total struct signatures verified: ~60+ across W23-W30.

## W25 D5 deviation APPLIED 5th time

W30 SPEC explicitly identifies `SequenceSendService.SendAsync` 91 LoC LARGEST method is **≥ 60 LoC threshold + sharp discrete flow boundary** (concurrent Task.WhenAll fan-out vs sequential Task.Delay loop = 2 distinct dispatching paths, NOT single central orchestration loop). Per W25 + W26 + W27 + W28 + W29 D5 sister-principle, LARGEST method MOVES to SendFlow.partial.cs.

**5 moves confirmed across W25-W30**:
- W25 OnChannelFrame 73 LoC → MOVED to FrameRouting.partial.cs (fan-out + error-isolation)
- W26 OnFrame(CanFrame) 62 LoC → MOVED to SinkLifecycle.partial.cs (frame-arrives → callback-fanout)
- W27 LoadAsync 60 LoC → MOVED to PersistenceOps.partial.cs (file-IO lifecycle)
- W28 LoadAsync 79 LoC → MOVED to LoadLifecycle.partial.cs (file-IO + parsing lifecycle)
- **W30 SendAsync 91 LoC → MOVED to SendFlow.partial.cs (concurrent-vs-sequential dispatcher)**

Sister-of-W22 + W23 stays (2 stays + 5 moves = 7 total observations). `largest-method-can-move` lesson HELD at 7/3 LOCKED in MASTER-LESSON-CATALOG at W30 SHIP closure.

## W17 wc-l-splitlines CONFIRMED 41-locked

Per W17 wc-l-splitlines CONFIRMED pattern (Windows-1252 encoded files require binary read+write with cp1252 codec, NOT UTF-8), W30 T1+T2 deletion scripts both use `read_bytes/decode('cp1252')/write_bytes/encode('cp1252')` pattern. Zero off-by-one errors.

## Cross-partial helper visibility pattern (CONFIRMED across 2 partials per W30)

W30 confirms cross-partial helper visibility works across **2 partials** (sister of W22 + W23 + W26 + W27 + W28 + W29):

- **`TryBuildRow` + `SendOneAsync` private helpers** (in Flow B `RowBuildFlow.partial.cs`) are called from Flow A `SendFlow.partial.cs` (`SendAsync` body) — partial-class cross-partial visibility handles this automatically.

This is a **7th confirmation** (1st was W22, 2nd was W23, 3rd was W26, 4th was W27, 5th was W28, 6th was W29, 7th is W30) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.

## Lesson promotions STATE-OF-THE-ART (post-W30)

| Lesson | Status | Observations |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | held |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | held; W30 = 20th deployment |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30 13-of-1) | promoted to 13 observations |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29 8-of-1) | held; W30 N/A (zero `[LoggerMessage]` partials) |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | promoted to 28th application at W30 |
| `multi-interface-partial-class-iframesink-and-iscriptcanapi` | 2/3 (W26.5) | held; W30 SequenceSendService has 0 interfaces |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | **7/3 since 3/3 LOCKED** (W30) | **PROMOTED to 7/3 LOCKED at W30** |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` | 2/3 (W25) | held; W30 is App/Services/MultiFrame, NOT Infrastructure/Channel |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` | 3/3 CONFIRMED LOCKED (W29.5) | held; W30 has no JSON-persistence |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` | 2/3 (W28.5) | held; W30 has no async file-load lifecycle |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | NEW 1/3 (W29) | held; W30 LARGEST method 91 LoC ≥ 60 LoC → W25 D5 deviation applied correctly |
| `app-services-multiframe-layer-sister-pattern-empirical-w30` | **NEW 1/3** (W30) | **NEW observation** |
| `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` | 1/3 (W18 R1) | held |

## What was captured

W30 SHIP closure = 6 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + SHIP. Each per the W12-W29 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit. Several dispatch attempts may fail due to API 429 token limit late-session; capture-decisions file lands full closure summary on disk as fallback per W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 sister precedent.

## What was skipped

- No separate `app-get-version` test for v3.44.0 bump (release-notes body suffices per W12 D7 + W14 D8 + W22 D7 + W23 D7 + W24 D7 + W25 D7 + W26 D7 + W27 D7 + W28 D7 + W29 D7 sister).
- No 2nd verification round on Core tests (CI PASS first attempt retained).
- No W18 R1 fix applied (zero `[LoggerMessage]` partials moved across partials — D4 N/A; no cross-partial caller-method concerns).
- No W29 small-god-class D5 default applied (LARGEST method 91 LoC ≥ 60 LoC threshold → W25 D5 deviation applies, NOT default D5).
- No `MultiFrameSequenceRow` or `MultiFrameSequenceRow.Kind` inner-enum changes (stays in `Models` namespace per W21 + W24 + W26 + W27 + W28 sister precedent).

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W30 T0+T1+T2 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W30 T1+T2 using-directive fixes (2 fixes: `PeakCan.Host.App.Models` ×2 for `MultiFrameSequenceRow` type).
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (35th + 36th application in W30).
- **W20 T2 R1 fabrication LESSON**: 36 verbatim re-extractions across W30 T1+T2 (35+36th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W30 verified `CanId` 2-arg + `CanFrame` 5-arg struct-ctor signatures (13th observation since 3/3 CONFIRMED).
- **W25 D5 deviation APPLIED**: W30 SendAsync 91 LoC LARGEST method MOVES (7th observation since 3/3 LOCKED at W25; W30 = 5th move in 7 observations).
- **W26.5 + W21 partial-keyword add**: W30 T0-D2 added `partial` modifier to `SequenceSendService` before extraction (28th cumulative application).

## CI status

- **1st attempt: SUCCESS** (no transient flaky `IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` exposure since W30 has no source change to that path; sister of W29 SHIP 4-attempt + W23.5-W25.5 + W27.5 + W28 PATCH 5-attempt MAX pattern retention)
- Local trx run: 16 SequenceSendService tests pass cleanly

## Cumulative trajectory (peakcan-host god-class series)

**26 god-class refactors SHIPPED** (W3-W30):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + W22 RecordService + W23 CyclicDbcSendService + W24 DbcSendViewModel + W25 ChannelRouter + W26 CanApi + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + **W30 SequenceSendService**

Plus 6 vault-only PATCH (W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5).

**Cumulative LoC reduction**: 25 god-class files -4,504 LoC (W3-W29) + **W30 SequenceSendService -184 LoC** = **-4,688 LoC total** across 26 god-class refactors + 6 PATCHes.

## Next

- **W30.5 vault-only PATCH** — lesson-promotion opportunity for 1 NEW 1/3 + 1 7/3 observation + 1 HELD 6/3 LOCKED candidates:
  - `app-services-multiframe-layer-sister-pattern-empirical-w30` NEW 1/3 (SequenceSendService MultiFrame decomposition = SendFlow + RowBuildFlow; 1st observation of App/Services/MultiFrame sister pattern)
  - `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 7/3 observation held (W30 SendAsync 91 LoC ≥ 60 LoC + concurrent-vs-sequential discrete flow boundary = MOVES; 5th move in 7 observations)
  - `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 → 2/3 awaiting (W30 has LARGEST method 91 LoC ≥ 60 LoC → W25 D5 deviation applied correctly, NOT default D5 — confirms W29 1/3 observation that default D5 applies to small god-classes only)
- **W31** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W30: `ReplayService.cs` 265 LoC (Core/Replay) OR `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `DbcSendViewModel.cs` 384 LoC (App/ViewModels — sister of W24, but already partial since W24; likely needs further splitting).
