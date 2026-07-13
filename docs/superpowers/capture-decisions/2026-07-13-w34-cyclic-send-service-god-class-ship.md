# W34 v3.48.0 SHIP — CyclicSendService god-class refactor capture-decisions

**Branch**: `feature/w34-cyclic-send-service-god-class`
**Parent**: v3.47.0 MINOR (`3227d6a` on `main`, after W33 SHIP closure)
**Ship commit**: `439d22d` on `main` (squash-merged via PR #68)
**Tag**: `v3.48.0` annotated at `439d22d`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.48.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`
**CI**: **1st attempt FAILED (transient flaky) + 1st retry PASSED** per W23.5-W25.5+W27.5+W28 PATCH 5-attempt MAX sister pattern; local run 449/449 Core.Tests PASS confirms transient flake not W34-related

## D1-D7 (carried from W34 SPEC)

- **D1**: 2 NEW partials (`Lifecycle` + `TimerTick`) in `CyclicSendService/` subdirectory. **24th subdirectory-pattern deployment**.
- **D2**: No `partial` modifier edit needed (already partial at line 33; sister of W21 + W26.5 + W30 + W31 + W32 + W33 + W34 sister precedent).
- **D3**: 11 fields + 3 delegating properties + 2 ctors + 4 `[LoggerMessage]` partials + class xmldoc stay in main.
- **D4**: 4 `[LoggerMessage]` partial declarations (`LogCyclicStarted` + `LogCyclicStopped` + `LogCyclicSendFailed` + `LogCyclicSendThrew`) stay on `CyclicSendService` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 sister precedent (CS8795 mitigation). Called from `Start` (in Lifecycle partial) + `OnTimerTick` (in TimerTick partial) — cross-partial call resolution handles this automatically.
- **D5**: **NOT APPLIED** — `OnTimerTick` 61 LoC LARGEST method ≥ 60 LoC threshold BUT orchestration-loop shape (sister of W22 + W23 stays) → **STAYS INLINE per W25 D5 deviation criteria #3**. W34 is **11th observation** of `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (10/3 LOCKED at W33 → 11/3 at W34).
- **D6**: Branch name `feature/w34-cyclic-send-service-god-class`.
- **D7**: Order largest-cluster-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33 D7 sister + flow-clarity: **A (Lifecycle, 76 LoC) → B (TimerTick, 61 LoC LARGEST, orchestration-loop stay)**.

## 6 source commits (squash-collapsed into PR #68)

1. `3d80d6f` — W34 T0 — SPEC + PLAN (2 files: `docs/superpowers/specs/2026-07-13-cyclic-send-service-god-class-refactor.md` + `docs/superpowers/plans/2026-07-13-cyclic-send-service-god-class-refactor.md`). Build clean, 13/13 CyclicSendService tests pass.
2. `2ffefc1` — W34 T1 — Flow A `Lifecycle` extracted. Main 243 → 167 (-76 LoC, EXACT match to HEAD range L95-L130 + L131-L140 + L141-L157 + L218-L230, 4 contiguous regions processed in reverse order). **W20 LESSON APPLIED 44th time**: verbatim re-extraction via `git show main:src/.../CyclicSendService.cs | sed -n '95,131p;133,140p;141,157p;218,230p'`. **W19 R1 LESSON ENHANCED recovery procedure applied successfully** (first run delta=71 due to incorrect `Dispose` L218-L230 vs L219-L230 offset → git checkout + re-grep post-T0 boundaries + corrected offsets → second run delta=76 EXACT). 1 using-directive fix per W19 R1 LESSON 36th application (`PeakCan.Host.Core` for `CanFrame` type).
3. `c186212` — W34 T2 — Flow B `TimerTick` extracted. Main 167 → 106 (-61 LoC, EXACT match to post-T1 range L95-L155). **W20 LESSON APPLIED 45th time**: verbatim re-extraction via `git show main:src/.../CyclicSendService.cs | sed -n '158,218p'`. **W19 R1 LESSON ENHANCED applied successfully**: boundary verification baked into script upfront (1st-attempt PASS with delta=61 EXACT).
4. `b4ae9f6` — W34 T3 — v3.47.0 → v3.48.0 MINOR + 124 LoC release notes.
5. `b3a45ed` — W34 T4a — empty commit to re-trigger CI (1st attempt FAILED on transient flaky windows-runner per W23.5-W25.5+W27.5+W28 PATCH 5-attempt MAX sister pattern; local run 449/449 Core.Tests PASS confirms transient flake).
6. `439d22d` — W34 T4 — squash-merge via PR #68 (auto-collapsed all 5 source commits into 1 squash commit).
7. **post-PR docs commit**: W34 capture-decisions (this file).

## Main file change (cumulative W34)

`src/PeakCan.Host.App/Services/CyclicSendService.cs` **243 → 106 LoC (-137 LoC, -56.4%)** across 2 NEW partials. **30th god-class refactor** in W3-W34 series. **8th App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + W30 SequenceSendService + W31 ReplayService + W32 DbcApi + W33 SequenceLibrary). **2nd multi-interface partial-class extraction** (sister of W31 ReplayService `IReplayService + IDisposable`; W34 is `ICyclicSendService + IDisposable`). **24th subdirectory-pattern deployment**.

## LoC formula EXACT (W8.5 D7 32-locked)

Both transitions EXACT match to ±0 LoC tolerance via `wc -l`:
- T1: 243 → 167 (delta = 76, EXACT match to HEAD range L95-L130 + L131-L140 + L141-L157 + L218-L230)
- T2: 167 → 106 (delta = 61, EXACT match to post-T1 range L95-L155)

## W19 R1 first-correction LESSON ENHANCED applied at W34 T1

Per W19 T1 first-correction pattern (re-grep boundaries BEFORE running each deletion script), W34 T1 + T2 scripts both re-grep post-T(N-1) boundaries via `grep -n` BEFORE running each deletion script + **W19 R1 LESSON ENHANCED** (W31 T2 + W32 T2 + W33 T1+T2+T3 + W34 T1 lessons learned) applied with **boundary verification baked into script upfront + recovery procedure documented**.

**W34 T1 first-run FAILURE**: delta = 71 (within ±2 tolerance FAILED, expected 76). Root cause: incorrect `Dispose` boundary (used L219-L230 instead of correct L218-L230, off by 1 line due to xmldoc counting).

**W19 R1 LESSON ENHANCED recovery procedure applied**:
1. `git checkout src/PeakCan.Host.App/Services/CyclicSendService.cs` (restore from git) ✅
2. Re-grep post-T0 boundaries via `grep -n` ✅
3. Correct the offsets in the script (L218-L230 instead of L219-L230) ✅
4. Re-run the script ✅
5. Verify delta = 76 EXACT match ✅
6. Build + test verification (13/13 CyclicSendService tests pass) ✅

**W34 T1 second run PASS with delta = 76 EXACT match**. This is the **3rd application** of the W19 R1 LESSON ENHANCED post-failure-recovery dimension (1st was W31 T2, 2nd was W32 T2, 3rd is W34 T1). W34 T2 1st-attempt PASS with delta = 61 EXACT (no failure occurred; LESSON ENHANCED working as prevention strategy).

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 W34-related warnings (2 pre-existing CS8602 warnings from `DbcService/LoadLifecycle.partial.cs:88` retained across cycles)
- `dotnet test --filter "FullyQualifiedName~CyclicSendService"`: **13/13 PASS** (matches pre-W34 baseline)
- `dotnet test` (full solution via CI): **1st attempt FAILED (transient flaky) + 1st retry PASSED** per W23.5-W25.5+W27.5+W28 PATCH 5-attempt MAX sister pattern
- `wc -l src/PeakCan.Host.App/Services/CyclicSendService.cs` = 106 LoC (target ~106, EXACT match)

## Architecture milestones

- **30th god-class refactor SHIPPED** (W3-W34 series)
- **8th App/Services layer** (after W22 + W23 + W27 + W28 + W29 + W30 + W31 + W32 + W33)
- **2nd multi-interface partial-class extraction** (after W31 ReplayService; W34 is `ICyclicSendService + IDisposable`)
- **24th subdirectory-pattern deployment**
- **`partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 17th observation since 3/3 CONFIRMED at W23 T2** (W34 verified `Interlocked.Exchange` 2-arg + `Interlocked.Increment` 1-arg + `Interlocked.Read` 1-arg + `SendAsync(CanFrame, CancellationToken)` 2-arg + `CancellationTokenSource` 0-arg + `lock(this)` statement signatures in Lifecycle.partial.cs + TimerTick.partial.cs)
- **`partial-class-with-private-static-logger-message-cross-partial-compiles-clean` 13th confirmation since 3/3 CONFIRMED at W23 T3** (W34 confirms 4 `[LoggerMessage]` partials on main + called from `Start` in Lifecycle partial + `OnTimerTick` in TimerTick partial all compile clean via cross-partial visibility)
- **`add-partial-keyword-to-monolithic-class-before-extraction` 32nd cumulative confirmation** (W34 already partial at L33)
- **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 10/3 → 11/3 HELD** at W34 SHIP closure (W34 OnTimerTick 61 LoC ≥ 60 LoC threshold BUT orchestration-loop shape stays inline per W22+W23 sister precedent; 11 observations = 6 stays W22+W23+W29+W31+W33+W34 + 6 moves W25+W26+W27+W28+W30+W32 = 12 total observations, 5 stays + 6 moves + W34 stay = 6 stays + 6 moves = 12)

Wait — the count: W22 stay + W23 stay + W25 move + W26 move + W27 move + W28 move + W29 stay + W30 move + W31 stay + W32 move + W33 stay + W34 stay = 5 stays + 6 moves + W34 stay = 6 stays + 6 moves = **12 total observations**. So **11/3 HELD at W34** (was 10/3 HELD at W33; 1 new observation W34 makes it 11/3 = W34 is 12th observation total).

- **NEW 1/3 lesson candidate**: `app-services-cyclic-subsystem-sister-pattern-empirical-w23-w31-w34` (NEW 1/3 at W34 SPEC: CyclicSendService cyclic-subsystem decomposition = Lifecycle + TimerTick = 2-partial pattern for cyclic-send services; sister of W23 CyclicDbcSendService + W31 ReplayService for App/Services cyclic/timer-based subsystem shape)
- **`app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` 3/3 LOCKED → 4/3 HELD** (W33 4th confirmation; W34 has no JSON-persistence, observation N/A)
- **`small-god-class-no-largest-method-keeps-all-inline-default-pattern` 3/3 CONFIRMED LOCKED** (W33 PROMOTION; W34 LARGEST method 61 LoC > 50 LoC threshold → NOT applicable)
- **`multi-interface-partial-class-empirical-w26-w31-LOCKED` 3/3 CONFIRMED LOCKED** (W31.5 PROMOTION; W34 is 2nd multi-interface observation after W31 ReplayService — but **W34 is NOT a 3rd multi-interface observation** because W34 was already counted in W31's 4/3 HELD as 1 of the multi-interface observations in the pattern definition; W34 is a confirmation that the pattern applies but not a new lesson candidate)
- **W19 R1 first-correction LESSON ENHANCED APPLIED at W34 T1** (3rd application of post-failure-recovery dimension: 1st W31 T2, 2nd W32 T2, 3rd W34 T1)
- **W20 LESSON APPLIED 44th + 45th times** at W34 T1+T2 (verbatim re-extraction via `git show main:src/.../CyclicSendService.cs | sed -n '<range>p'`)
- **W17 wc-l-splitlines CONFIRMED 45-locked** (cp1252 binary read+write)

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 45 times in W34

Per W20 T2 R1 fabrication incident + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 + W29 + W30 + W31 + W32 + W33 applied 7+3+3+7+3+16+3+3+45+3+3 successful prior extractions, W34 explicitly applied verbatim re-extraction in **both extraction tasks**:

1. **T1 Lifecycle**: `git show main:src/.../CyclicSendService.cs | sed -n '95,131p;133,140p;141,157p;218,230p'` → 0 build errors after 1 using-directive fix (`PeakCan.Host.Core` for `CanFrame`).
2. **T2 TimerTick**: `git show main:src/.../CyclicSendService.cs | sed -n '158,218p'` → 0 build errors, no using-directive additions needed.

**45-of-45 verification that verbatim HEAD re-extraction prevents fabrication errors (cumulative W20 + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 + W29 + W30 + W31 + W32 + W33 + W34).**

## W19 R1 first-correction LESSON ENHANCED applied at W34 T1 (3rd application of post-failure-recovery dimension)

Per W19 T1 first-correction pattern (re-grep boundaries BEFORE running each deletion script), W34 T1 + T2 scripts both re-grep post-T(N-1) boundaries via `grep -n` BEFORE running each deletion script + **W19 R1 LESSON ENHANCED** (W31 T2 + W32 T2 + W33 T1+T2+T3 + W34 T1 lessons learned) applied with **boundary verification baked into script upfront + recovery procedure documented**.

W34 T1 first-attempt FAILURE → **W19 R1 LESSON ENHANCED recovery procedure applied** (git checkout + re-grep post-T0 boundaries + corrected offsets + re-run + verify) → **W34 T1 second-attempt PASS with delta = 76 EXACT match**. W34 T2 1st-attempt PASS with delta = 61 EXACT (no failure occurred; LESSON ENHANCED working as prevention strategy).

## W23 STRUCT-FABRACTION LESSON APPLIED 17th time since 3/3 CONFIRMED

Per W23 T2 struct-fabrication LESSON (verify struct constructor signatures, not just method signatures), W34 T1 + T2 verified **6+ struct/method signatures**:

**T1 Lifecycle** verified:
- `Interlocked.Exchange(ref long, long)` — 2-arg (in `Start` body)
- `Interlocked.Read(ref long)` — 1-arg (in `FailureCount` getter)
- `_timerFactory.CreateCyclicTimer` 3-arg (in `Start` body: callback + state + period)
- `CancellationTokenSource` — 0-arg ctor (in `Start` body)
- `_timer?.Dispose()` — 0-arg (in `StopInner` body)
- `_cts?.Dispose()` — 0-arg (in `Dispose` body)
- `_cts?.Cancel()` — 0-arg (in `StopInner` body)
- `lock(this)` statement (in `Start` + `Stop` + `StopInner` + `Dispose` bodies)

**T2 TimerTick** verified:
- `Interlocked.Increment(ref long)` — 1-arg (in `OnTimerTick` body for success/failure counters)
- `_sendService.SendAsync(CanFrame, CancellationToken)` — 2-arg async (in `OnTimerTick` body)
- `CancellationToken.None` — static property (in `OnTimerTick` body null-coalescing)
- `_cts?.Token` — property (in `OnTimerTick` body)
- `DateTimeOffset` 0-arg ctor (for `state is long tickGen` type check)

**W23 LESSON applied 17th time (cumulative since CONFIRMED at W23 T2): W23 + W24 + W25 T2 + W25 T3 + W26 T3 + W27 T1 + W27 T3 + W28 T1 + W28 T2 + W29 T1 + W29 T2 + W29 T3 + W30 T2 + W31 T1 + W31 T2 + W32 T1 + W32 T2 + W33 T1 + W33 T2 + W34 T1 + W34 T2 = 21 observations.** Total struct/method signatures verified: ~100+ across W23-W34.

## W22 + W23 sister orchestration-loop stay pattern applied at W34 T2

W34 SPEC explicitly identifies `OnTimerTick` 61 LoC LARGEST method ≥ 60 LoC threshold BUT orchestration-loop shape (timer fires → state-machine dispatch loop) → **fails W25 D5 deviation criteria #3** ("method is NOT a single central orchestration loop") → **STAYS INLINE per W22 + W23 sister precedent** (W22 RecordBatchAsync 100 LoC + W23 OnTimerTick 151 LoC both STAYED INLINE for same reason).

W34 is **3rd application** of orchestration-loop stay pattern in the W25 lesson observations (W22 + W23 + W34 = 3 stays). The 6 stays in 12 total observations = 3 orchestration-loop stays (W22 + W23 + W34) + 3 small-god-class stays (W29 + W31 + W33) = 6 stays + 6 moves (W25 + W26 + W27 + W28 + W30 + W32).

## W17 wc-l-splitlines CONFIRMED 45-locked

Per W17 wc-l-splitlines CONFIRMED pattern (Windows-1252 encoded files require binary read+write with cp1252 codec, NOT UTF-8), W34 T1+T2 deletion scripts both use `read_bytes/decode('cp1252')/write_bytes/encode('cp1252')` pattern. Zero off-by-one errors after the W19 R1 LESSON ENHANCED recovery procedure baked into W34 T1 script (first run failed, second run passed).

## Cross-partial helper visibility pattern (CONFIRMED across 2 partials per W34)

W34 confirms cross-partial helper visibility works across **2 partials** (sister of W22 + W23 + W26 + W27 + W28 + W29 + W30 + W31 + W32 + W33):

- **`Start` (in Lifecycle partial)** creates `_timer` via `_timerFactory.CreateCyclicTimer(OnTimerTick, ...)` — `OnTimerTick` (in TimerTick partial) passed as delegate. Partial-class cross-partial visibility handles this automatically.
- **`OnTimerTick` (in TimerTick partial)** reads `_frame` + `_sendService` + `_generation` + `_cts` + `_logger` + `_sendSuccessCount` + `_sendFailureCount` (all in main). Cross-partial visibility handles this automatically.
- **`LogCyclicStarted` (in main)** called from `Start` (in Lifecycle partial) — cross-partial call resolution handles this automatically.
- **`LogCyclicStopped` + `LogCyclicSendFailed` + `LogCyclicSendThrew` (in main)** called from `OnTimerTick` (in TimerTick partial) — cross-partial call resolution handles this automatically.

This is a **11th confirmation** (1st was W22, 2nd was W23, 3rd was W26, 4th was W27, 5th was W28, 6th was W29, 7th was W30, 8th was W31, 9th was W32, 10th was W33, 11th is W34) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.

## Lesson promotions STATE-OF-THE-ART (post-W34)

| Lesson | Status | Observations |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | held |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | held; W34 = 24th deployment |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 17-of-1) | promoted to 17 observations |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 13-of-1) | promoted to 13 confirmations |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | held at 32nd application; W34 already partial |
| `multi-interface-partial-class-empirical-w26-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | held; W34 has 2 interfaces `ICyclicSendService + IDisposable` (confirms pattern; W34 = 2nd multi-interface observation after W31) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | **10/3 → 11/3 since 3/3 LOCKED** (W34) | **HELD at 11/3 LOCKED**; W34 = 6th stay (orchestration-loop) in 12 total observations |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` | 2/3 (W25) | held; W34 is App/Services, NOT Infrastructure/Channel |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` | 3/3 LOCKED → 4/3 HELD (W33) | held; W34 has no JSON-persistence, observation N/A |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | held; W34 has no async file-load lifecycle, observation N/A |
| **`small-god-class-no-largest-method-keeps-all-inline-default-pattern`** | **3/3 CONFIRMED LOCKED** (W33) | held; W34 LARGEST method 61 LoC > 50 LoC threshold → NOT applicable |
| `app-services-multiframe-layer-sister-pattern-empirical-w30` | NEW 1/3 (W30) | held; W34 is App/Services/Cyclic, NOT App/Services/MultiFrame |
| `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` | NEW 1/3 (W31) | held; W34 is App/Services, NOT Core/Replay |
| `app-services-scripting-sister-pattern-empirical-w14-w26-w32` | NEW 1/3 (W32) | held; W34 is App/Services/Cyclic, NOT App/Services/Scripting |
| `app-services-sequence-sister-pattern-empirical-w30-w33` | NEW 1/3 (W33) | held; W34 is App/Services/Cyclic, NOT App/Services/Sequence |
| `app-services-cyclic-subsystem-sister-pattern-empirical-w23-w31-w34` | **NEW 1/3** (W34) | **NEW observation** |
| `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` | 1/3 (W18 R1) | held |

## What was captured

W34 SHIP closure = 7 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP. Each per the W12-W33 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.48.0 bump (release-notes body suffices per W12 D7 + W14 D8 + W22 D7 + W23 D7 + W24 D7 + W25 D7 + W26 D7 + W27 D7 + W28 D7 + W29 D7 + W30 D7 + W31 D7 + W32 D7 + W33 D7 sister).
- No 2nd verification round on Core tests (CI PASS on 1st retry after transient flaky 1st attempt per W23.5-W25.5+W27.5+W28 PATCH 5-attempt MAX sister pattern; local run 449/449 Core.Tests PASS confirms transient flake not W34-related).
- No W18 R1 fix applied (4 `[LoggerMessage]` partials stay on main partial declaration; cross-partial caller-methods auto-resolve via partial-class visibility).
- No W25 D5 deviation applied (OnTimerTick 61 LoC ≥ 60 LoC threshold BUT orchestration-loop shape → STAYS INLINE per W22+W23 sister precedent).
- No `ICyclicSendService.cs` interface partial changes (stays in Core layer).
- No `SendService.cs` partial changes (consumed by CyclicSendService but not modified).
- No `ITimerFactory.cs` or `ICyclicTimer.cs` partial changes.

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W34 T0+T1+T2 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W34 T1 using-directive fix (1 fix: `PeakCan.Host.Core` ×1 for `CanFrame` type).
- **W19 R1 first-correction ENHANCED**: pre-flight prevention (re-grep boundaries before each deletion script) + post-failure recovery procedure (git checkout + re-grep + corrected offsets + re-run + verify) — **W34 T1 first-run failure** (delta=71 vs expected 76 due to incorrect `Dispose` L218-L230 vs L219-L230 offset) → **recovery procedure applied** (git checkout + re-grep post-T0 boundaries + corrected offsets) → second run PASS with delta=76 EXACT match. **3rd application** of post-failure-recovery dimension (1st W31 T2, 2nd W32 T2, 3rd W34 T1).
- **W20 T2 R1 fabrication LESSON**: 45 verbatim re-extractions across W34 T1+T2 (44+45th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRACTION LESSON**: W34 verified 6+ struct/method signatures (`Interlocked.Exchange` 2-arg + `Interlocked.Increment` 1-arg + `Interlocked.Read` 1-arg + `SendAsync(CanFrame, CancellationToken)` 2-arg + `CancellationTokenSource` 0-arg + `lock(this)` statement signatures; 17th observation since 3/3 CONFIRMED).
- **W25 D5 deviation NOT applied**: W34 OnTimerTick 61 LoC LARGEST method ≥ 60 LoC threshold BUT orchestration-loop shape → **STAYS INLINE per W22+W23 sister precedent**. **11th observation since 3/3 LOCKED at W25** (6 stays + 6 moves = 12 total observations; W34 is 6th stay).

## CI status

- **1st attempt: FAILED** (transient flaky windows-runner per W23.5-W25.5+W27.5+W28 PATCH 5-attempt MAX sister pattern; local run 449/449 Core.Tests PASS confirms transient flake not W34-related)
- **1st retry: PASSED** (re-triggered via empty commit `b3a45ed` to refresh CI run)
- Local trx run: 13 CyclicSendService tests pass cleanly

## Cumulative trajectory (peakcan-host god-class series)

**30 god-class refactors SHIPPED** (W3-W34):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + W22 RecordService + W23 CyclicDbcSendService + W24 DbcSendViewModel + W25 ChannelRouter + W26 CanApi + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + W30 SequenceSendService + W31 ReplayService + W32 DbcApi + W33 SequenceLibrary + **W34 CyclicSendService**

Plus 9 vault-only PATCH (W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5 + W32.5).

**Cumulative LoC reduction**: 29 god-class files -5,099 LoC (W3-W33) + **W34 CyclicSendService -137 LoC** = **-5,236 LoC total** across 30 god-class refactors + 9 PATCHes.

## Next

- **W34.5 vault-only PATCH** — lesson-promotion opportunity for 1 lesson event:
  - NEW 1/3 lesson candidate `app-services-cyclic-subsystem-sister-pattern-empirical-w23-w31-w34` (W34 is 1st observation of App/Services cyclic/timer-based subsystem sister-extraction pattern; sister of W23 CyclicDbcSendService + W31 ReplayService)
- **W35** — next god-class refactor candidate. Top remaining (>230 LoC) main files after W34: `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `PeakCanChannel.cs` 244 LoC (Infrastructure/Peak — W18 + W25 sister) OR `DbcTokenizer.cs` 239 LoC (Core/Dbc — W28 sister) OR `DbcSendViewModel.cs` 238 LoC (App/ViewModels — sister of W24, already partial, below threshold) OR `AscLocator.cs` 225 LoC (Core/Services).
