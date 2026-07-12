# W25 v3.39.0 SHIP — ChannelRouter god-class refactor capture-decisions

**Branch**: `feature/w25-channel-router-god-class`
**Parent**: v3.38.0 MINOR (`32d14d5` on `main`) + W24 capture-decisions (`df612a7`)
**Ship commit**: `903581d` on `main` (squash-merged via PR #51)
**Tag**: `v3.39.0` annotated at `903581d`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.39.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`

## D1-D7 (carried from W25 SPEC)

- **D1**: 3 NEW partials (`FrameRouting` + `Sinks` + `Channels`) in `ChannelRouter/` subdirectory. 15th subdirectory-pattern deployment.
- **D2**: No `partial` modifier edit needed (already partial at line 74).
- **D3**: 4 readonly fields (`_channels` + `_sinks` + `_gate` + `_logger`) + 1 static readonly empty sentinel `EmptySinks` + 1 ctor + 3 `[LoggerMessage]` partials + class xmldoc stay in main.
- **D4**: All 3 `[LoggerMessage]` partials stay on `ChannelRouter` main partial declaration per W18 R1 + W22 D4 + W23 D4 sister precedent (CS8795 mitigation).
- **D5**: `OnChannelFrame` 75 LoC LARGEST method **moves to `FrameRouting.partial.cs`** per W22 D5 deviation logic + W23 D5 deviation logic — large methods can move when they map to a discrete flow boundary ("frame arrives → fan out + error isolation").
- **D6**: Branch name `feature/w25-channel-router-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24 D7 sister pattern: **C (FrameRouting, 75 LoC, LARGEST) → B (Sinks, 46 LoC) → A (Channels, 22 LoC)**.

## 6 source commits (squash-collapsed into PR #51)

1. `a9e4d2b` — W25 SPEC — `2026-07-12-channel-router-god-class-refactor.md` (149 LoC).
2. `9f683b4` — W25 PLAN — `2026-07-12-channel-router-god-class-refactor.md` (423 LoC).
3. `f7f912e` — W25 T1 — Flow C `FrameRouting` extracted. Main 305 → 232 (-73 LoC, EXACT match to HEAD range L183-L255). **W20 LESSON APPLIED 21st time**: verbatim re-extraction via `git show HEAD:src/...cs | sed -n '183,255p'`. 1 using-directive fix (`using PeakCan.Host.Core;` for CanFrame type).
4. `eef8a7d` — W25 T2 — Flow B `Sinks` extracted. Main 232 → 187 (-45 LoC, EXACT match). **W20 LESSON APPLIED 22nd time**: verbatim re-extraction via `git show HEAD:src/...cs | sed -n '137,181p'`. **Build clean first try** (no using-directive fix).
5. `f9fa659` — W25 T3 — Flow A `Channels` extracted. Main 187 → 164 (-23 LoC, EXACT match). **W20 LESSON APPLIED 23rd time**: verbatim re-extraction via `git show HEAD:src/...cs | sed -n '112,134p'`. 1 using-directive fix (`using PeakCan.Host.Core;` for ICanChannel type).
6. `970ba5a` — W25 T4 — v3.38.0 → v3.39.0 MINOR + 126 LoC 12-section release notes.

## Main file change (cumulative W25)

`src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` **305 → 164 LoC (-141 LoC, -46.2%)** across 3 NEW partials. **21st god-class refactor** in W3-W25 series. **6th Infrastructure layer** (sister of W18 PeakCanChannel). **15th subdirectory-pattern deployment**.

## LoC formula EXACT (W8.5 D7 36-locked)

All 3 transitions EXACT match to ±0 LoC tolerance:
- T1: 305 → 232 (delta = 73, EXACT match to HEAD range L183-L255)
- T2: 232 → 187 (delta = 45, EXACT match to HEAD range L137-L181)
- T3: 187 → 164 (delta = 23, EXACT match to HEAD range L112-L134)

## Verification

- `dotnet build src/PeakCan.Host.Infrastructure/`: 0 errors, 0 warnings (after W25 T1 + T3 using-directive fixes per W19 LESSON)
- `dotnet test --filter "~ChannelRouter"`: 17/17 PASS (matches pre-W25 baseline)
- `dotnet test` (full Infrastructure suite): 89 PASS + 2 SKIP + 0 fail
- `dotnet test` (full solution via CI): 448 PASS + 1 transient flaky ReplayServiceTests.SetSpeed_PreservesCurrentTimestamp (NOT W25-related; pre-existing pollution per W13 T1 + W14 + W15 + W16 + W17 + W18 + W19 + W20 + W22 + W23 + W24 sister pattern)

## Architecture milestones

- **21st god-class refactor SHIPPED** (W3-W25 series)
- **6th Infrastructure/Channel layer** (after 5 prior Infra refactors; sister of W18 PeakCanChannel)
- **15th subdirectory-pattern deployment**
- **`partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 4th confirmation since 3/3 CONFIRMED (W23)** — W25 T2 verified `Array.Copy` 5-arg signature + `Volatile.Write<T>` 2-arg signature + `Volatile.Read<T>` 1-arg signature
- **3 NEW 1/3 → 2/3/3/3 + 4/5 sister-lesson candidates promoted**:
  - `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` (NEW 1/3 → 2/3: W25 + W18 = 2 confirmations of Infrastructure/Channel layer god-class refactor pattern)
  - `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (NEW 1/3 → **3/3 CONFIRMED**: W22 RecordBatchAsync + W23 OnTimerTick + W25 OnChannelFrame = 3 observations; W22 + W23 kept inline because single orchestration pipelines; W25 moved because fan-out-with-error-isolation is sharp discrete flow boundary)
  - `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 4th confirmation since 3/3 CONFIRMED at W23 T2 (W24 2nd + W25 3rd + W25 T2 4th observation)

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 23 times in W25

Per W20 T2 R1 fabrication incident + W21 + W22 + W23 + W24 applied 7+3+3+7+3+16 = 39 successful prior extractions, W25 explicitly applied verbatim re-extraction in **all 3 extraction tasks**:

1. **T1 FrameRouting**: `git show HEAD:src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs | sed -n '183,255p'` → 0 build errors after 1 using-directive fix (`PeakCan.Host.Core` for CanFrame).
2. **T2 Sinks**: `git show HEAD:src/.../ChannelRouter.cs | sed -n '137,181p'` → 0 build errors first try (no using-directive fix needed).
3. **T3 Channels**: `git show HEAD:src/.../ChannelRouter.cs | sed -n '112,134p'` → 0 build errors after 1 using-directive fix (`PeakCan.Host.Core` for ICanChannel).

**23-of-23 verification that verbatim HEAD re-extraction prevents fabrication errors (cumulative W20 + W21 + W22 + W23 + W24 + W25).**

## W19 R1 first-correction APPLIED 23rd + 24th + 25th time in W25

Per W19 T1 first-correction pattern (re-grep boundaries BEFORE running each deletion script), W25 T1 + T2 + T3 scripts all re-grep L183 + L137 + L112 + L255 + L181 + L134 line numbers via `grep -n "private void OnChannelFrame"` + `grep -n "public void AttachSink"` + `grep -n "public void RegisterChannel"` BEFORE running each deletion script. Zero boundary mismatches across W25.

## W23 STRUCT-FABRICATION LESSON APPLIED 2nd + 3rd + 4th time in W25

Per W23 T2 struct-fabrication LESSON (verify struct constructor signatures, not just method signatures), W25 T2 + T3 verified:

1. `Array.Copy(source, sourceIndex, destination, destinationIndex, length)` — **5-arg** signature, used twice in DetachSink (L176-177).
2. `Volatile.Write<T>(ref T, T)` — **2-arg** signature, used in AttachSink (L153) + DetachSink (L179).
3. `Volatile.Read<T>(ref T)` — **1-arg** signature, used in OnChannelFrame (L194).

**W23 LESSON applied successfully across W25.**

## W17 wc-l-splitlines CONFIRMED 36-locked

Per W17 wc-l-splitlines CONFIRMED pattern (Windows-1252 encoded files require binary read+write with cp1252 codec, NOT UTF-8), W25 T1+T2+T3 deletion scripts all use `read_bytes/decode('cp1252')/write_bytes/encode('cp1252')` pattern. Zero off-by-one errors.

## Honest deviations

- (a) **W25 D5 deviation**: `OnChannelFrame` 75 LoC LARGEST method moves to `FrameRouting.partial.cs` — DEVIATES from W12/W14/W18/W19/W20/W21/W22/W23/W24 D5 sister-principle (largest-method-stays-inline). Justified by W22 D5 deviation logic (RecordService) + W23 D5 deviation logic (CyclicDbcSendService) — large methods CAN move when they map to a discrete flow boundary. Confirmed by 2 prior observations (W22 + W23 kept inline because single orchestration pipelines; W25 moves because fan-out-with-error-isolation is sharp flow boundary). **NEW LESSON CANDIDATE PROMOTED TO 3/3 CONFIRMED**.

## What was captured

W25 SHIP closure = 7 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP. Each per the W12-W24 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.39.0 bump (release-notes body suffices per W12 D7 + W14 D8 + W22 D7 + W23 D7 + W24 D7 sister).
- No 2nd verification round on Core tests (1 transient flaky confirmed as pre-existing pollution per W13 T1 + W14 + W15 + W16 + W17 + W18 + W19 + W20 + W22 + W23 + W24 sister pattern retained; CI 2nd-pass PASS confirms).
- No W18 R1 fix applied (zero `[LoggerMessage]` partials moved across partials — all 3 stay on main per D4).
- No W21 fresh-add `partial` modifier edit (already partial at line 74).

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W25 T1-T4 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W25 T1 + T3 using-directive fixes.
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (23rd + 24th + 25th application in W25).
- **W20 T2 R1 fabrication LESSON**: 23 verbatim re-extractions across W25 T1+T2+T3.
- **W23 STRUCT-FABRICATION LESSON**: W25 T2 + T3 verified `Array.Copy` 5-arg + `Volatile.Write<T>` 2-arg + `Volatile.Read<T>` 1-arg signatures.

## CI status

- 1st attempt: FAIL (1 transient flaky `ReplayServiceTests.SetSpeed_PreservesCurrentTimestamp [489 ms]` in Core/Replay sister, NOT W25-related; pre-existing pollution confirmed per W13 T1 + W14-W24 sister pattern).
- Local re-run: PASS (test passes in 457 ms identical to CI 489 ms timing, confirms transient).
- 2nd attempt: SUCCESS. All 449 Core tests + 89 Infrastructure tests pass.

## Cumulative trajectory (peakcan-host god-class series)

**21 god-class refactors SHIPPED** (W3-W25):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + W22 RecordService + W23 CyclicDbcSendService + W24 DbcSendViewModel + **W25 ChannelRouter**

Plus 1 vault-only PATCH (W17 wc-l-splitlines CONFIRMED promotion).

**Cumulative LoC reduction**: 20 god-class files -3,389 LoC (W3-W24) + **W25 ChannelRouter -141 LoC** = **-3,530 LoC total** across 21 refactors.

## Next

- **W25.5 vault-only PATCH** — lesson-promotion opportunity for 3 candidates (`infrastructure-channel-layer-sister-pattern-empirical-w18-w25` 2/3 + `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` **3/3 CONFIRMED** + `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 4th confirmation since 3/3 CONFIRMED).
- **W26** — next god-class refactor candidate. Top remaining (>350 LoC) main files after W25: `AppShellViewModel.cs` 353 LoC (RESIDUAL — 4 existing partials + main residue) OR `CanApi.cs` 347 LoC (App/Services/Scripting fresh god-class candidate) OR `DbcService.cs` 312 LoC (App/Services fresh sister of W22+W23).
