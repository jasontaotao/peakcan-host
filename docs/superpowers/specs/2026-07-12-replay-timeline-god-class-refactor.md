# W15 Spec — ReplayTimeline god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` from 469 LoC to ~250 LoC by extracting 2 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Sister pattern to W14 ScriptEngine refactor. The class is **already `internal sealed partial class ReplayTimeline`** at line 13 (modifier pre-exists for future split). The refactor adds 2 partial-class files (NOT 4 like W13/W14 — fewer because OnTick is so large it deserves its own partial, and the 6 PlaybackLifecycle methods cluster). Each partial file owns one logical flow group. Main file keeps the 18 fields + 7 properties + 1 ctor + SetFrames + 7 `[LoggerMessage]` partial declarations. `internal sealed` is the **first core-class god-class with `internal` visibility** to receive the partial split — sister of W10 DbcParser (public static), W12 UdsClient (public instance IDisposable), W13 AscParser (public static nested sealed partial), W14 ScriptEngine (public sealed partial IDisposable).

**Tech Stack:** C# .NET 10, Core layer (no WPF/MVVM). Git with LF line endings.

## Global Constraints

- **Public/internal API unchanged.** No method signatures, properties, or event types move. `internal sealed` visibility preserved.
- **partial-class visibility.** All private methods + private fields visible across partial files.
- **Test coverage unchanged.** No tests added, removed, or modified. **No xmldoc-grep tests** for ReplayTimeline (verified — only behavior tests via constructor calls per W12 D8 + W13 D8 sister observations).
- **Line-ending normalized to LF.**
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 3.** Tasks 1-2 keep `src/Directory.Build.props` at v3.29.0. Task 3 bumps to v3.30.0.
- **Branch**: `feature/w15-replay-timeline-god-class` (created from `main` @ `5ff9a35` v3.29.0).

---

## Current state (469 LoC)

`src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` (v3.29.0 HEAD) has:
- 1 `internal sealed partial class ReplayTimeline` with **7 methods** (1 ctor + SetFrames + Play + Pause + Seek + SetSpeed + Stop + OnTick + PlayedTimestamp helper) + 7 properties (CurrentTimestamp + Speed + IsPlaying + Loop + StartTimestamp + EndTimestamp + HasStarted)
- 18 fields: 5 readonly (lock + logger + emit + onPlaybackEnded + onSinkThrew + onLoopRewound + activeLoopRegionGetter) + 11 mutable state fields (_frames + _nextFrameIndex + _currentTimestamp + _speed + _isPlaying + _hasStarted + _loop + _startTimestamp + _endTimestamp + _playStartWallClock + _playStartTimestamp + _timer + _sinkException)
- 7 `[LoggerMessage]` partial declarations (4 LogPlay + 3 LogOnTick)

Threshold: 800 LoC ceiling. ReplayTimeline at **58.6%** of ceiling.

## Target state (~250 LoC main + 2 partials)

```
src/PeakCan.Host.Core/Replay/ReplayTimeline.cs                          # main file, ~250 LoC after Task 2
src/PeakCan.Host.Core/Replay/ReplayTimeline/                            # NEW directory
  PlaybackLifecycleFlow.cs                                                 # Task 1 -- Play + Pause + Seek + SetSpeed + Stop + PlayedTimestamp (~95 LoC)
  OnTickFlow.cs                                                            # Task 2 -- OnTick (largest, playback tick) (~178 LoC)
docs/superpowers/plans/2026-07-12-replay-timeline-god-class-refactor.md  # NEW in Task 0
docs/release-notes-v3.30.0.md                                             # NEW in Task 3
```

**Net reduction**: 469 → ~250 LoC main (-219 LoC, -46.7%); total lines unchanged (~469 across main + partials).

## Flow boundaries

All flows are instance methods on the `internal sealed partial class ReplayTimeline`. Each partial-class file declares `internal sealed partial class ReplayTimeline { ... }` and adds the flow's methods.

### Flow — Main file (declaration + properties + SetFrames + 7 LoggerMessage partials)

**Stays in main**:
- `using` block (lines 1-3)
- Namespace + class xmldoc (lines 5-12)
- Outer class declaration (line 13) — already `internal sealed partial`, no change needed
- 18 fields (lines 15-57) — state ownership
- 1 ctor (lines 59-82) — initialization
- 7 properties (lines 84-131): CurrentTimestamp, Speed, IsPlaying, Loop, StartTimestamp, EndTimestamp, HasStarted
- `SetFrames(IReadOnlyList<ReplayFrame>)` (lines 133-141) — state-ownership (writes `_frames` field); trivial body
- 7 `[LoggerMessage]` partial declarations (lines 431-468) — source-generator scope, stay in main per W10+W11+W12+W13+W14 sister-lesson

### Flow A — PlaybackLifecycle (~95 LoC)

**Methods**:
- `public void Play()` (lines 143-165) — wire-up: log entry + lock + early-return if already playing + early-return if no frames + reset wallclock/playStart + set _isPlaying + create 1ms Timer if first time + log started
- `public void Pause()` (lines 167-174) — lock + set _isPlaying = false
- `public void Seek(double timestamp)` (lines 176-190) — lock + reset cursor state (currentTimestamp + playStartTimestamp + wallclock + walk _nextFrameIndex forward)
- `public void SetSpeed(double multiplier)` (lines 192-222) — validate + lock + re-anchor wallclock FIRST (v3.16.9.3 PATCH) + update _currentTimestamp via PlayedTimestamp
- `public void Stop()` (lines 224-236) — lock + reset all state + dispose Timer + null
- `private double PlayedTimestamp { get; }` (lines 242-249) — wall-clock-adjusted timestamp helper

**Depends on**:
- `_frames`, `_nextFrameIndex`, `_currentTimestamp`, `_speed`, `_isPlaying`, `_hasStarted`, `_loop`, `_playStartWallClock`, `_playStartTimestamp`, `_timer`, `_sinkException` (main fields)
- `_lock`, `_logger`, `_emit` (main fields)
- `LogPlayEntry`, `LogPlayAlreadyRunning`, `LogPlayNoFrames`, `LogPlayStarted` (main LoggerMessage partials)

**Rationale for grouping**: All 6 methods mutate the timeline's playback-state fields under one lock. They share the `_isPlaying`, `_playStartWallClock`, `_playStartTimestamp` triple as a unit. OnTick reads these fields but never mutates `_playStartTimestamp`/`_playStartWallClock` directly (it goes through PlayedTimestamp). So Flow A owns user-facing control; Flow B owns tick-driven progress. Each partial is sized appropriately.

### Flow B — OnTick (~178 LoC)

**Methods**:
- `private void OnTick(object? state)` (lines 251-429) — playback tick: log entry + iterate frames (range filter at iteration boundary + emit batch + A/B loop-region rewind + EOF handling + sink-throw capture) + raise callbacks OUTSIDE the lock.

**Depends on**:
- ALL 18 fields (main fields, reads + writes most of them under `_lock`)
- `_emit`, `_onPlaybackEnded`, `_onSinkThrew`, `_onLoopRewound`, `_activeLoopRegionGetter` (main fields; delegates/callbacks)
- `LogOnTickEntry`, `LogOnTickNotPlaying`, `LogOnTickEmitting`, `LogInvalidLoopRegion` (main LoggerMessage partials)
- `PlaybackEndedEventArgs` (in same namespace)

**Rationale for grouping**: OnTick is 178 LoC — too large to share a partial with anything else (one-method-one-partial preserves readability per W12 D7). Sibling of UdsClient's OnMessageReceived (Flow A Transport) and ScriptEngine's ExecuteScript (Flow A ExecutionLifecycle) — both moved as their own one-method-one-partial sister to W15's OnTick one-method-one-partial.

## Architecture invariants (per W3-W14 patterns)

1. **API unchanged.** `internal` visibility preserved; same methods/properties callable.
2. **partial-class visibility**: private methods + private fields visible across partials. `OnTick` (Flow B) reads/writes all 18 fields (main). `Play/Pause/Seek/SetSpeed/Stop` (Flow A) mutate state and `OnTick` reads.
3. **State stays close to its owner**: 18 fields stay in main (W11 D5 sister). `SetFrames` stays in main (writes `_frames` field; field-ownership co-location).
4. **LoggerMessage partials in main**: 7 `[LoggerMessage]` partial declarations stay in main per W10+W11+W12+W13+W14 sister-lesson. Calls from Flow A + Flow B reach them via partial-class visibility.
5. **No new files outside the established directory**: `ReplayTimeline/` is a sibling to `ReplayTimeline.cs` (sister of `DbcParser/`, `UdsClient/`, `AscParser/`, `ScriptEngine/` precedent).
6. **Instance class with `internal sealed partial` visibility modifier**: 4th class-shape in the partial-extraction repertoire (after public static, public instance, public sealed partial). `internal sealed partial` has identical partial-mechanism semantics — only accessibility differs.

## Verification

- `dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore`: 0 errors
- `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Replay"`: all tests pass without modification (ReplayService tests + ReplayTimeline tests)
- `dotnet test --no-restore --nologo -c Debug`: full solution builds clean, no regressions

## Risk notes

- **R1 (low)**: Missing `using` directives — per W3-W14 CONFIRMED lesson. Pre-scan. Likely 0 new usings (Flow A uses `System` only; Flow B uses `System.Collections.Generic` for `List<ReplayFrame>`).
- **R2 (low)**: LoC formula — per W8.5 D7 CONFIRMED 11-locked. Apply `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. **W13 T1 2/3 loose-assertion pattern** applied.
- **R3 (low)**: OnTick is 178 LoC — exceeds 50 LoC threshold. Per W14 D8 sister-lesson (ExecuteScript 155 LoC stayed inline per W12 D7 sister-principle), OnTick stays inline. One-method-one-purpose (single playback tick); body has clear sections but **splitting them into private helpers would require changing the method shape** (current is one continuous lock-region-emit-region-call-back block). Decision recorded as W15 D5.
- **R4 (very low)**: `internal sealed partial class` visibility modifier — sister of W10 DbcParser (public static partial) + W13 AscParser (public static partial) + W14 ScriptEngine (public sealed partial). The `internal` keyword doesn't change partial-mechanism semantics; just limits accessor scope. Compile + test seam unaffected.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W14 CONFIRMED direct partial-class visibility is sufficient.
- **No sub-class creation**: ReplayTimeline stays a single class.
- **No test changes**: All existing tests stay unmodified.
- **No public/internal API surface change**.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A — `PlaybackLifecycleFlow` (Play + Pause + Seek + SetSpeed + Stop + PlayedTimestamp — 6 methods total, ~95 LoC).
2. **Task 2**: Extract Flow B — `OnTickFlow` (single 178 LoC method).
3. **Task 3**: Bump version v3.29.0 → v3.30.0 + write release notes (MINOR ship commit).
4. **Task 4**: Tier-3 push + tag + GH release.

Total: 4 tasks, ~3 source commits (1 per flow + 1 ship).

## Decision log

- **D1**: 2 partials with descriptive names (`PlaybackLifecycleFlow` / `OnTickFlow`).
- **D2**: **2 partials NOT 4** — combining user-facing control methods (Flow A 95 LoC) + tick-driven OnTick (Flow B 178 LoC) is the natural partition. Subdividing further would scatter related fields across partials (R3 sister risk).
- **D3**: Branch name `feature/w15-replay-timeline-god-class`.
- **D4**: Order tasks: **A (PlaybackLifecycle, 95 LoC) → B (OnTick, 178 LoC largest)** — PlaybackLifecycle first (smaller, simpler state-ownership boundary); OnTick second (largest, validates one-method-one-partial sister principle per W14 D8).
- **D5**: OnTick 178 LoC stays inline per W12 D7 + W14 D8 sister-lessons (one-method-one-purpose = single playback tick; helper-extraction would require changing the method shape). Captured as 1/3 NEW sister-lesson candidate `one-method-one-partial-does-not-imply-must-helper-extract` (or NO, same as W12 D7).
- **D6**: Plan LoC-trajectory-table formula explicitly applied per W8.5 D7 CONFIRMED. Use W13 T1 2/3 loose-assertion pattern.

## Closing milestone context

This is the **12th god-class refactor** in the project (W3-W15 series). ReplayTimeline is the **5th Core layer** god-class (W9 IsoTpLayer + W10 DbcParser + W12 UdsClient + W13 AscParser + W15 ReplayTimeline). **First god-class with `internal sealed partial` visibility modifier** — `internal` keyword (vs `public`) tested for partial extraction. Validates the partial-class split pattern works for: instance class with `internal sealed partial` + Timer-based playback scheduler + lifecycle state machine.

If W15 ships + tests pass + lesson confirmations hold, W15.5 vault-only PATCH (lesson-promotion) and W16 (next candidate: `ReplayViewModel.cs` ~462 LoC, ViewModel class with already-extracted `.Loader.partial.cs`) become natural next steps.
