# W34 SPEC — CyclicSendService god-class refactor (30th overall, 8th App/Services layer)

**Date**: 2026-07-13
**Target class**: `src/PeakCan.Host.App/Services/CyclicSendService.cs` (243 LoC)
**Target version**: v3.48.0 MINOR
**Sister pattern**: W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + W30 SequenceSendService + W31 ReplayService + W32 DbcApi + W33 SequenceLibrary (9 prior god-class refactors). **30th god-class refactor** in W3-W33 series.

## Context

`CyclicSendService` (243 LoC) is the **8th App/Services layer** god-class candidate in the W3-W33 series (29 refactors shipped, 9 prior god-class sisters). The class orchestrates cyclic CAN frame send control (start/stop/dispose + timer-tick dispatch) for the App layer.

**Class shape** (already verified via direct read):

- `public sealed partial class CyclicSendService : ICyclicSendService, IDisposable` (L33) — **already partial** (W21 + W26.5 3/3 CONFIRMED + W30 + W31 + W32 + W33 sister precedent; no D2 needed)
- 8 fields: `_sendService` + `_logger` + `_timerFactory` + `_timer` + `_frame` + `_interval` + `_generation` + `_sendSuccessCount` + `_sendFailureCount` + `_isRunning` + `_cts` (11 total — see L35-L51)
- 3 delegating properties: `IsRunning` (L52) + `SuccessCount` (L62) + `FailureCount` (L68)
- 2 ctors: production ctor (L70-L74, 5 LoC) + internal test ctor (L85-L98, 14 LoC) — **STAY INLINE per W21+W24+W26+W27+W28+W29+W30+W31+W32+W33 sister DI-wiring-boilerplate pattern**
- 3 public methods: `Start(CanFrame, TimeSpan)` (32 LoC with xmldoc) + `Stop()` (8 LoC) + `Dispose()` (12 LoC)
- 2 private helpers: `StopInner()` (17 LoC) + `OnTimerTick(object?)` (**61 LoC LARGEST method** — ≥ 60 LoC threshold)
- 4 `[LoggerMessage]` partial declarations: `LogCyclicStarted` + `LogCyclicStopped` + `LogCyclicSendFailed` + `LogCyclicSendThrew` (L232-L242) — **STAY ON MAIN per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 sister precedent (CS8795 mitigation)**

**LARGEST method analysis** per W25 D5 deviation:

- `OnTimerTick` 61 LoC ≥ **60 LoC threshold** ✓
- Shape: **timer-tick state-machine dispatch loop** (timer fires → send frame → update counters → handle exceptions) — **sister of W23 CyclicDbcSendService.OnTimerTick 151 LoC STAYED INLINE**
- Per W12 + W14 + W18 + W19 + W20 + W21 + W22 + W23 D5 default sister-principle + **W25 D5 deviation criteria #3** ("method is NOT a single central orchestration loop"): `OnTimerTick` IS a state-machine dispatch loop → **fails criteria #3** → **STAYS INLINE**
- **W25 D5 deviation NOT applied** — sister of W22 + W23 stays (orchestration loops ≥ 60 LoC)
- **W34 is NOT a small god-class** (LARGEST method 61 LoC > 50 LoC threshold) → **NOT applicable to W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 3/3 LOCKED** (that pattern applies to god-classes with LARGEST method < 50 LoC)

**Sister-extraction sequence** (App/Services cyclic/timer-based subsystem):

- W23 CyclicDbcSendService (2 partials: TickLifecycle + CyclicOps) — **EXPLICIT sister of W34** (both cyclic-send services in App/Services subsystem)
- W31 ReplayService (2 partials: FileIoLifecycle + FrameEmission) — has timer-tick via `_timeline.Play()` + `_timeline.OnTick` (sister of cyclic pattern via different mechanism)
- **W34 CyclicSendService (2 partials: Lifecycle + TimerTick)** — explicit timer-tick handler (OnTimerTick is timer-tick dispatch loop, sister of W23 OnTimerTick 151 LoC)

## W34 D1-D7

- **D1**: 2 NEW partials (`Lifecycle` + `TimerTick`) in `CyclicSendService/` subdirectory. **24th subdirectory-pattern deployment**.
- **D2**: No `partial` modifier edit needed (already partial at line 33; W21 + W26.5 + W30 + W31 + W32 + W33 sister precedent).
- **D3**: 11 fields + 3 delegating properties + 2 ctors + 4 `[LoggerMessage]` partials + class xmldoc stay in main.
- **D4**: 4 `[LoggerMessage]` partial declarations (`LogCyclicStarted` + `LogCyclicStopped` + `LogCyclicSendFailed` + `LogCyclicSendThrew`) stay on `CyclicSendService` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 sister precedent (CS8795 mitigation). Called from `Start` (in Lifecycle partial) + `OnTimerTick` (in TimerTick partial) — cross-partial call resolution handles this automatically.
- **D5**: **NOT APPLIED** — `OnTimerTick` 61 LoC LARGEST method ≥ 60 LoC threshold BUT orchestration-loop shape (sister of W22 + W23 stays) → **STAYS INLINE per W25 D5 deviation criteria #3**. W34 is **11th observation** of `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (10/3 LOCKED at W33 → 11/3 at W34 = 6 stays W22+W23+W29+W31+W33+W34 + 6 moves W25+W26+W27+W28+W30+W32 = 12... wait, let me recount).
- **D6**: Branch name `feature/w34-cyclic-send-service-god-class`.
- **D7**: Order largest-cluster-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33 D7 sister + flow-clarity: **A (Lifecycle, 89 LoC, lifecycle cluster) → B (TimerTick, 61 LoC LARGEST, orchestration-loop stay)**. Lifecycle first since it has the lifecycle state-management (`_isRunning` + `_cts` + `_timer` lifecycle); TimerTick second since it's the LARGEST method but orchestration-loop stays inline.

## Architecture

Sister pattern of W22 + W23 + W27 + W28 + W29 + W30 + W31 + W32 + W33 (subdirectory + non-suffix `.partial.cs` filenames). 30th god-class refactor. **8th App/Services layer** + **24th subdirectory-pattern deployment** + **2nd multi-interface partial-class extraction** (sister of W31 ReplayService `IReplayService + IDisposable`).

### Flow boundaries (Phase 1 verified)

**Stays in main (~93 LoC)**:
- `using` block (L1-L7) + namespace (L9) + class xmldoc (L11-L31)
- `public sealed partial class CyclicSendService : ICyclicSendService, IDisposable` (L33, already partial)
- 11 fields (L35-L51)
- 3 delegating properties: `IsRunning` (L52) + `SuccessCount` (L62) + `FailureCount` (L68)
- 2 ctors: production (L70-L74) + internal test (L85-L98)
- 4 `[LoggerMessage]` partial declarations (L232-L242)

**Flow A — Lifecycle (~89 LoC, T1) → `CyclicSendService/Lifecycle.partial.cs`**:

- 1 public method `Start(CanFrame, TimeSpan)` (L97-L130, 34 LoC xmldoc+body)
- 1 public method `Stop()` (L133-L140, 8 LoC)
- 1 private helper `StopInner()` (L141-L157, 17 LoC)
- 1 public method `Dispose()` (L219-L230, 12 LoC)

Plus 2 ctors (L70-L98, 19 LoC) stay in main per W21+W24+W26+W27+W28+W29+W30+W31+W32+W33 sister "ctor = DI wiring boilerplate" pattern.

**Flow B — TimerTick (~61 LoC, T2) → `CyclicSendService/TimerTick.partial.cs`**:

- 1 private async method `OnTimerTick(object? state)` (L158-L218, **61 LoC LARGEST method, STAYS INLINE per W22+W23 sister orchestration-loop stay pattern**)

**Cross-partial caller pattern**: `Start` (in Lifecycle partial) creates `_timer` via `_timerFactory.CreateCyclicTimer(OnTimerTick, ...)` — `OnTimerTick` (in TimerTick partial) passed as delegate to `_timer` ctor. Partial-class cross-partial visibility handles this automatically (sister of W22+W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33 cross-partial helper pattern).

## LoC trajectory (W8.5 D7 32-locked + W19 R1 first-correction ENHANCED + W17 wc-l-splitlines CONFIRMED + W20 fabrication LESSON APPLIED 43+ times in W33 + W23 STRUCT-FABRACTION LESSON APPLIED 16 times)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — Lifecycle | L97-L130 + L133-L140 + L141-L157 + L219-L230 = 89 LoC (4 contiguous regions, processed in reverse order) | 89 | 1 | 154 |
| T2 | B — TimerTick | L158-L218 = 61 LoC (1 contiguous region, orchestration-loop stay) | 61 | 1 | 93 |
| T3 | v3.47.0 -> v3.48.0 | (no source) | 0 | 0 | 93 |
| T4 | ship | -- | -- | -- | 93 |

Cumulative: 243 -> 154 -> 93 main. **Re-grep + range verify after each task per W19 R1 ENHANCED (pre-flight prevention + post-failure recovery)**.

## W20 + W23 LESSON APPLIED — verbatim re-extract + struct-ctor verification

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle struct-constructor fabrication, W34 will:

1. **Re-grep boundaries BEFORE running each deletion script** (Phase 1 done above; re-verify before T1 + T2 script runs).
2. **Re-extract original code from main HEAD via `git show main:src/.../CyclicSendService.cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **CRITICAL: Verify `Interlocked.Exchange(ref long, long)` 2-arg + `Interlocked.Read(ref long)` 1-arg + `_timerFactory.CreateCyclicTimer` 3-arg + `CanFrame` struct 4-arg ctor + `TimeSpan` 1-arg ctor + `CancellationTokenSource` 0-arg ctor + `lock(this)` statement signatures** — W23 LESSON applied (sister of W29 + W33 struct-fabrication verification).
4. **Verify `[LoggerMessage]` attribute signatures for 4 declarations** — D4 sister-pattern.
5. **Build + run filter tests after each task** to catch any extraction errors immediately.

## Tasks

### T0: Branch + SPEC + PLAN commits

```bash
git checkout -b feature/w34-cyclic-send-service-god-class main
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~CyclicSendService" --logger "console;verbosity=minimal"
git add docs/superpowers/specs/2026-07-13-cyclic-send-service-god-class-refactor.md
git commit -m "W34 spec: CyclicSendService god-class refactor (2 partials + 5-task roll-out, 30th overall, 8th App/Services, 24th subdirectory-pattern deployment, 2nd multi-interface partial extraction)"
git add docs/superpowers/plans/2026-07-13-cyclic-send-service-god-class-refactor.md
git commit -m "W34 plan: CyclicSendService god-class refactor (2 partials: Lifecycle + TimerTick)"
```

### T1: Lifecycle partial (~89 LoC)

Write `scripts/w34_task1_delete_lifecycle.py` with W19 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern + **W19 R1 LESSON ENHANCED boundary verification upfront + recovery procedure documented** (per W31 T2 + W32 T2 + W33 T1+T2+T3 lessons learned). 4 contiguous regions: `Dispose` L219-L230 + `StopInner` L141-L157 + `Stop` L133-L140 + `Start` L97-L130, processed in REVERSE order. Expected: 243 - 89 + 1 = 154 LoC. Build + tests, commit.

### T2: TimerTick partial (~61 LoC)

Re-grep post-T1 ranges. Write `scripts/w34_task2_delete_timertick.py`. 1 contiguous region: `OnTimerTick` L158-L218 (post-T1 line numbers). Expected: 154 - 61 + 1 = 93 LoC. Build + tests, commit.

### T3: v3.47.0 → v3.48.0 MINOR + release notes

Mirror W33 release notes format. MINOR (2 NEW partial extractions = architectural change).

### T4: Tier-3 ship

`gh pr create` → `--squash --delete-branch` → `git tag v3.48.0` → `gh release create`.

## Sister-lesson candidates to monitor

| Lesson | Status | What W34 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W34 9th god-class application (T1+T2) — 44th application total |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33 16-of-1) | W34 17th observation (`Interlocked.Exchange` 2-arg + `Interlocked.Read` 1-arg + `_timerFactory.CreateCyclicTimer` 3-arg + `CancellationTokenSource` 0-arg + `lock(this)` statement signatures verified) |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33 12-of-1) | W34 13th confirmation (4 `[LoggerMessage]` partials on main + called from `Start` in Lifecycle partial + `OnTimerTick` in TimerTick partial) |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | W34 already partial (32nd cumulative confirmation) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | 10/3 since 3/3 LOCKED (W33) | **W34 11th observation** (OnTimerTick 61 LoC ≥ 60 LoC threshold BUT orchestration-loop shape stays inline per W22+W23 sister precedent; 11 observations = 5 stays W22+W23+W29+W31+W33 + 6 moves W25+W26+W27+W28+W30+W32 + 1 stay W34 = 6 stays + 6 moves = 12 total... let me recount) |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | W34 24th deployment, sister-of-W33 |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` | 3/3 CONFIRMED LOCKED (W29.5) | N/A — W34 has no JSON-persistence |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | N/A — W34 has no async file-load lifecycle (synchronous Start/Stop + async OnTimerTick for send) |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | 3/3 CONFIRMED LOCKED (W33) | N/A — W34 LARGEST method 61 LoC > 50 LoC threshold → NOT applicable |
| `app-services-multiframe-layer-sister-pattern-empirical-w30` | NEW 1/3 (W30) | N/A — W34 is App/Services/Cyclic, NOT App/Services/MultiFrame |
| `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` | NEW 1/3 (W31) | N/A — W34 is App/Services, NOT Core/Replay |
| `app-services-scripting-sister-pattern-empirical-w14-w26-w32` | NEW 1/3 (W32) | N/A — W34 is App/Services/Cyclic, NOT App/Services/Scripting |
| `app-services-sequence-sister-pattern-empirical-w30-w33` | NEW 1/3 (W33) | N/A — W34 is App/Services/Cyclic, NOT App/Services/Sequence |
| `multi-interface-partial-class-empirical-w26-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | N/A — W34 CyclicSendService has 2 interfaces `ICyclicSendService + IDisposable` (multi-interface!) — **W34 is 3rd multi-interface observation after W26 + W31!** |
| `app-services-cyclic-subsystem-sister-pattern-empirical-w23-w31-w34` | **NEW W34 1/3** | W34 1st observation: CyclicSendService cyclic-subsystem decomposition (Lifecycle + TimerTick) = 2-partial pattern for cyclic-send services; sister of W23 CyclicDbcSendService (similar cyclic-tick pattern) + W31 ReplayService (timer-tick via `_timeline.Play()` mechanism) |

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~CyclicSendService"`: tests pass without modification
- `dotnet test` (full solution): 0 new fails
- `wc -l src/PeakCan.Host.App/Services/CyclicSendService.cs` ≤ 105 LoC (target ~93)
- 2 NEW partial files in `CyclicSendService/` directory
- 11 fields + 3 delegating properties + 2 ctors + 4 `[LoggerMessage]` partials remain in main
- DI registration unchanged (production DI binds `AddSingleton<ICyclicSendService, CyclicSendService>(...)` factory)
- Public API unchanged (`ICyclicSendService` interface + `IDisposable` interface)
- Tag v3.48.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes.
- No facade pattern (W3-W33 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No W18 R1 mitigation needed (D4 keeps all 4 `[LoggerMessage]` partials on main partial declaration).
- No `ICyclicSendService.cs` interface partial changes (stays in Core layer).
- No `SendService.cs` partial changes (consumed by CyclicSendService but not modified).
- No `ITimerFactory.cs` or `ICyclicTimer.cs` partial changes.
