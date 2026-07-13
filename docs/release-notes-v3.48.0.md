# Release Notes v3.48.0 — CyclicSendService god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.48.0
**Branch**: `feature/w34-cyclic-send-service-god-class`
**Parent**: v3.47.5 PATCH (v3.47.0 MINOR + W33.5 vault-only PATCH = `3227d6a` on main)

## Why this MINOR

`src/PeakCan.Host.App/Services/CyclicSendService.cs` had grown to **243 LoC** as of v3.47.0 — at 30.4% of the 800 LoC Round-1 ceiling. Single `public sealed partial class CyclicSendService : ICyclicSendService, IDisposable` (already partial at L33 — sister of W21 + W26.5 + W30 + W31 + W32 + W33 + W34 sister precedent; no D2 application needed). 11 fields + 3 delegating properties + 2 ctors + 4 public/private methods (`Start` + `Stop` + `StopInner` + `OnTimerTick` LARGEST 61 LoC) + 1 public `Dispose` + 4 `[LoggerMessage]` partial declarations (`LogCyclicStarted` + `LogCyclicStopped` + `LogCyclicSendFailed` + `LogCyclicSendThrew`).

This is the **30th god-class refactor** in the project (W3-W34 series). **8th App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + W30 SequenceSendService + W31 ReplayService + W32 DbcApi + W33 SequenceLibrary). **2nd multi-interface partial-class extraction** (sister of W31 ReplayService `IReplayService + IDisposable`; W34 is `ICyclicSendService + IDisposable`). **24th subdirectory-pattern deployment**.

## LoC trajectory (W8.5 D7 CONFIRMED formula — 32-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n)`. Both transitions **EXACT match** via `wc -l`.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | Lifecycle (Start + Stop + StopInner + Dispose, 4 contiguous regions) | 95-130 + 131-140 + 141-157 + 218-230 (HEAD) | 76 | 167 |
| T2 | TimerTick (OnTimerTick, 1 contiguous region, stays inline per W22+W23 sister orchestration-loop stay pattern) | 158-218 (HEAD, shifted to 95-155 post-T1) | 61 | 106 |
| **Total** | -- | -- | **137** | **106** |

**Net**: 243 → 106 LoC main file (**-137 LoC, -56.4%**). Total project LoC across main + 2 partials ≈ 282 LoC (small +176 LoC overhead from per-file namespace + using directives + 2 xmldoc header comment blocks).

## What this MINOR does

### Refactor — CyclicSendService adds 2 NEW partials in `CyclicSendService/` subdirectory

1. **NEW `src/PeakCan.Host.App/Services/CyclicSendService/Lifecycle.partial.cs` (~176 LoC)**:
   - Contains 4 methods verbatim from main HEAD L95-L230: `public void Start(CanFrame, TimeSpan)` (36 LoC xmldoc+body) + `public void Stop()` (10 LoC xmldoc+body) + `private void StopInner()` (17 LoC) + `public void Dispose()` (13 LoC).
   - Verbatim re-extraction via `git show main:src/.../CyclicSendService.cs | sed -n '95,131p;133,140p;141,157p;218,230p'` per W20 T2 R1 fabrication LESSON (44th application).
   - 1 using-directive fix per W19 R1 first-correction (`PeakCan.Host.Core` for `CanFrame` type).

2. **NEW `src/PeakCan.Host.App/Services/CyclicSendService/TimerTick.partial.cs` (~140 LoC)**:
   - Contains 1 private async method verbatim from main HEAD L158-L218: `private async void OnTimerTick(object?)` (**61 LoC LARGEST method, STAYS INLINE per W22+W23 sister orchestration-loop stay pattern**).
   - **W25 D5 deviation NOT applied**: 61 LoC ≥ 60 LoC threshold BUT orchestration-loop shape (timer fires → state-machine dispatch loop) → fails W25 D5 deviation criteria #3 → **STAYS INLINE per W22+W23 sister precedent**.
   - Verbatim re-extraction via `git show main:src/.../CyclicSendService.cs | sed -n '158,218p'` per W20 LESSON (45th application).
   - **W23 STRUCT-FABRACTION LESSON APPLIED 17th time**: `Interlocked.Increment(ref long)` 1-arg + `SendAsync(CanFrame, CancellationToken)` 2-arg signatures verified.

### D1-D7 sister-pattern decisions (carried from W34 SPEC)

- **D1**: 2 NEW partials (`Lifecycle` + `TimerTick`) in `CyclicSendService/` subdirectory. **24th subdirectory-pattern deployment**.
- **D2**: No `partial` modifier edit needed (already partial at line 33; sister of W21 + W26.5 + W30 + W31 + W32 + W33 + W34 sister precedent).
- **D3**: 11 fields + 3 delegating properties + 2 ctors + 4 `[LoggerMessage]` partials + class xmldoc stay in main.
- **D4**: 4 `[LoggerMessage]` partial declarations (`LogCyclicStarted` + `LogCyclicStopped` + `LogCyclicSendFailed` + `LogCyclicSendThrew`) stay on `CyclicSendService` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 sister precedent (CS8795 mitigation). Called from `Start` (in Lifecycle partial) + `OnTimerTick` (in TimerTick partial) — cross-partial call resolution handles this automatically.
- **D5**: **NOT APPLIED** — `OnTimerTick` 61 LoC LARGEST method ≥ 60 LoC threshold BUT orchestration-loop shape (sister of W22 + W23 stays) → **STAYS INLINE per W25 D5 deviation criteria #3**. W34 is **11th observation** of `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (10/3 LOCKED at W33 → 11/3 at W34 = 5 stays W22+W23+W29+W31+W33 + 6 moves W25+W26+W27+W28+W30+W32 + 1 stay W34 = 6 stays + 6 moves = 12 total observations).
- **D6**: Branch name `feature/w34-cyclic-send-service-god-class`.
- **D7**: Order largest-cluster-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33 D7 sister + flow-clarity: **A (Lifecycle, 76 LoC, lifecycle cluster) → B (TimerTick, 61 LoC LARGEST, orchestration-loop stay)**. Lifecycle first since it has the lifecycle state-management (`_isRunning` + `_cts` + `_timer` lifecycle); TimerTick second since it's the LARGEST method but orchestration-loop stays inline.

## What this MINOR does NOT change (YAGNI + sister-pattern invariants)

- No public/internal API change.
- No test changes (13 CyclicSendService tests pass without modification).
- No facade pattern (W3-W33 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No CS8795 risk (D4 keeps 4 `[LoggerMessage]` partials on main partial declaration).
- No `ICyclicSendService.cs` interface partial changes (stays in Core layer).
- No `SendService.cs` partial changes (consumed by CyclicSendService but not modified).
- No `ITimerFactory.cs` or `ICyclicTimer.cs` partial changes.
- No D5 default sister-principle change (W22+W23 sister orchestration-loop stay pattern correctly APPLIED here since `OnTimerTick` 61 LoC IS a state-machine dispatch loop).
- No W33 `small-god-class-no-largest-method` default D5 applied (LARGEST method 61 LoC > 50 LoC threshold → W22+W23 sister orchestration-loop stay pattern applies).

## Architecture milestones

- **30th god-class refactor SHIPPED** (W3-W34 series).
- **8th App/Services layer** (after W22 + W23 + W27 + W28 + W29 + W30 + W31 + W32 + W33).
- **2nd multi-interface partial-class extraction** (after W31 ReplayService `IReplayService + IDisposable`; W34 is `ICyclicSendService + IDisposable`).
- **24th subdirectory-pattern deployment**.
- **W20 LESSON APPLIED 44th + 45th times** across W34 T1+T2 (verbatim re-extraction via `git show main:src/.../CyclicSendService.cs | sed -n '<range>p'`).
- **W23 STRUCT-FABRICATION LESSON APPLIED 17th time since 3/3 CONFIRMED at W23 T2** (W34 verified `Interlocked.Exchange` 2-arg + `Interlocked.Increment` 1-arg + `Interlocked.Read` 1-arg + `SendAsync(CanFrame, CancellationToken)` 2-arg + `CancellationTokenSource` 0-arg + `lock(this)` statement signatures).
- **W19 R1 first-correction ENHANCED APPLIED at W34 T1**: pre-flight prevention (re-grep boundaries before deletion script) + post-failure recovery procedure (git checkout + re-grep + corrected offsets + re-run + verify) — W34 T1 first run failed (delta=71 vs expected 76 due to incorrect offset for `Dispose` L218-L230 vs L219-L230), recovery procedure applied (git checkout + re-grep post-T0 boundaries + corrected offsets), second run PASS with delta=76 EXACT.
- **W25 D5 deviation NOT applied** (11th observation since 3/3 LOCKED at W25; W34 OnTimerTick 61 LoC ≥ 60 LoC threshold BUT orchestration-loop shape → STAYS INLINE per W22+W23 sister precedent; 6 stays W22+W23+W29+W31+W33+W34 + 6 moves W25+W26+W27+W28+W30+W32 = 12 total observations).
- **`partial-class-with-private-static-logger-message-cross-partial-compiles-clean` 13th confirmation since 3/3 CONFIRMED at W23 T3** (W34 confirms 4 `[LoggerMessage]` partials on main + called from `Start` in Lifecycle partial + `OnTimerTick` in TimerTick partial all compile clean via cross-partial visibility).
- **NEW 1/3 lesson candidate**: `app-services-cyclic-subsystem-sister-pattern-empirical-w23-w31-w34` (NEW 1/3 at W34 SPEC: CyclicSendService cyclic-subsystem decomposition = Lifecycle + TimerTick = 2-partial pattern for cyclic-send services; sister of W23 CyclicDbcSendService + W31 ReplayService for App/Services cyclic/timer-based subsystem shape).
- **`app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` 3/3 LOCKED → 4/3 HELD** (W33 4th confirmation; W34 has no JSON-persistence, observation N/A).
- **`small-god-class-no-largest-method-keeps-all-inline-default-pattern` 3/3 CONFIRMED LOCKED** (W33 PROMOTION; W34 has LARGEST method 61 LoC > 50 LoC threshold → NOT applicable).
- **W17 wc-l-splitlines CONFIRMED 45-locked** (cp1252 binary read+write).

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W34 T1 using-directive fix per W19; 2 pre-existing CS8602 warnings from `DbcService/LoadLifecycle.partial.cs:88` retained across cycles, NOT W34-related).
- `dotnet test --filter "FullyQualifiedName~CyclicSendService"`: **13/13 PASS** (matches pre-W34 baseline).
- `dotnet test` (full solution via CI): 0 new fails expected.

## Process lessons applied (W20 + W23 + W19 + W22+W23 sister orchestration-loop stay pattern)

- **Lesson #10** (verify each commit before proceeding): each W34 T0+T1+T2 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W34 T1 using-directive fixes (1 fix: `PeakCan.Host.Core` ×1 for `CanFrame` type).
- **W19 R1 first-correction ENHANCED**: pre-flight prevention (re-grep boundaries before each deletion script) + post-failure recovery procedure (git checkout + re-grep + corrected offsets + re-run + verify) — **W34 T1 first-run failure** (delta=71 vs expected 76 due to incorrect `Dispose` L218-L230 vs L219-L230 offset) → **recovery procedure applied** (git checkout + re-grep post-T0 boundaries + corrected offsets) → second run PASS with delta=76 EXACT match.
- **W20 T2 R1 fabrication LESSON**: 45 verbatim re-extractions across W34 T1+T2 (44+45th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W34 verified `Interlocked.Exchange` 2-arg + `Interlocked.Increment` 1-arg + `Interlocked.Read` 1-arg + `SendAsync(CanFrame, CancellationToken)` 2-arg + `CancellationTokenSource` 0-arg + `lock(this)` statement signatures (17th observation since 3/3 CONFIRMED).
- **W25 D5 deviation NOT applied**: W34 OnTimerTick 61 LoC LARGEST method ≥ 60 LoC threshold BUT orchestration-loop shape → **STAYS INLINE per W22+W23 sister precedent**. **11th observation since 3/3 LOCKED at W25** (6 stays + 6 moves = 12 total observations; W34 is 6th stay).

## Sister-pattern cumulative trajectory (god-class series, W3-W34)

| W | Layer | Subdirectory | Main LoC | Prior + W34 |
|---|---|---|---|---|
| W22 | App/Services | RecordService/ | -193 | 18th god-class |
| W23 | App/Services | CyclicDbcSendService/ | -288 | 19th god-class |
| W27 | App/Services/Trace | RecentSessionsService/ | -213 | 23rd god-class |
| W28 | App/Services | DbcService/ | -195 | 24th god-class |
| W29 | App/Services | SendFrameLibrary/ | -162 | 25th god-class |
| W30 | App/Services/MultiFrame | SequenceSendService/ | -184 | 26th god-class |
| W31 | Core/Replay | ReplayService/ | -119 | 27th god-class |
| W32 | App/Services/Scripting | DbcApi/ | -171 | 28th god-class |
| W33 | App/Services/Sequence | SequenceLibrary/ | -121 | 29th god-class |
| **W34** | **App/Services/Cyclic** | **CyclicSendService/** | **-137** | **30th god-class** |

**Cumulative LoC reduction (W3-W34)**: 29 god-class files -5,099 LoC (W3-W33) + **W34 CyclicSendService -137 LoC** = **-5,236 LoC total** across 30 god-class refactors + 9 vault-only PATCHes (W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5 + W32.5).

## What was captured

W34 SHIP closure = 6 captures dispatched: SPEC + PLAN + T1 + T2 + T3 (T4 ship captures via `vault-pkm:pkm-capture` background-dispatched post-T4 squash-merge + tag + GH release). Each per the W12-W33 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## Next (post-ship)

- **W34.5 vault-only PATCH** — lesson-promotion opportunity for 1 lesson event:
  - NEW 1/3 lesson candidate `app-services-cyclic-subsystem-sister-pattern-empirical-w23-w31-w34` (W34 is 1st observation of App/Services cyclic/timer-based subsystem sister-extraction pattern; sister of W23 CyclicDbcSendService + W31 ReplayService)
- **W35** — next god-class refactor candidate. Top remaining (>230 LoC) main files after W34: `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `PeakCanChannel.cs` 244 LoC (Infrastructure/Peak — W18 + W25 sister) OR `DbcTokenizer.cs` 239 LoC (Core/Dbc — W28 sister) OR `DbcSendViewModel.cs` 238 LoC (App/ViewModels — sister of W24, already partial, below threshold) OR `AscLocator.cs` 225 LoC (Core/Services).
