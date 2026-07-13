# LESSON — Core-layer Replay subsystem sister-pattern (NEW 1/3 observation)

**Status**: NEW 1/3 observation at W31 SHIP closure (2026-07-13)
**Pattern name**: `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31`
**1st observation**: W31 v3.45.0 MINOR ReplayService god-class refactor
**Awaiting**: 2 more observations across any future Core/Replay god-class refactor to promote to 2/3 → 3/3 CONFIRMED → LOCKED

## Observation

`src/PeakCan.Host.Core/Replay/ReplayService.cs` (265 LoC, W31 god-class candidate) was the **1st Core/Replay subsystem god-class refactor** explicitly designed to decompose the `ReplayService` (the service that owns `ReplayTimeline`) into 2 NEW partials based on **file-IO lifecycle vs frame emission** flow-boundary clarity.

### Sister-extraction sequence (Core/Replay subsystem)

- **W15 ReplayTimeline** (separate class, not a partial): owns the timer-loop + per-tick frame dispatch
- **W22 RecordService** (Core layer, 2 partials: `Lifecycle` + `Mutators`): records CAN frames during a session
- **W31 ReplayService** (Core layer, 2 partials: `FileIoLifecycle` + `FrameEmission`): replays recorded frames from a file

W22 + W31 are both Core-layer god-classes; W15 ReplayTimeline is the foundational class that W31's ReplayService owns + delegates to via `_timeline.Play()` + `_timeline.Pause()` + `_timeline.SetFrames(_frames)` + `_timeline.Stop()` etc.

## Pattern (1st observation)

When refactoring a **Core/Replay subsystem god-class** (a service that orchestrates the replay of recorded CAN frames), decompose into **2 NEW partial-class files** using the **file-IO lifecycle vs frame emission** flow-boundary pattern:

### Partial 1 — `FileIoLifecycle.partial.cs`

Public `async Task LoadAsync(string path, CancellationToken ct = default)` method:
- **Defensive reset on entry**: clear `_frames` + push to `_timeline.SetFrames(_frames)` BEFORE the parse attempt
- **File-IO + parse**: `File.OpenRead(PathNormalizer.Normalize(path))` + `AscParser.ParseAsync(fs, ct)`
- **Exception-wrapping**: wrap `FileNotFoundException` + `IOException` into `ReplayLoadException` (rethrow `ReplayException` unchanged)
- **State mutation**: assign `_frames` + delegate to `_timeline.SetFrames(_frames)`

Plus a `public void Reset()` helper method:
- Clear `_frames` + delegate to `_timeline.Stop()` + `_timeline.SetFrames(_frames)`

### Partial 2 — `FrameEmission.partial.cs`

4 private helpers (frame-emission flow cluster):
- `EmitFrame(ReplayFrame)` — tri-state CAN-ID filter check + `Task.Run` fire-and-forget sink dispatch + `FrameEmitted?.Invoke(frame)` event raise
- `EmitFrameToSinkAsync(ReplayFrame)` — `await _sink.SendFrameAsync(frame, CancellationToken.None)`
- `OnSinkThrewFromTimeline(Exception)` — capture-first-exception (set-once) + `_timeline.Pause()` + `RaisePlaybackEnded(new PlaybackEndedEventArgs(ex))`
- `RaisePlaybackEnded(PlaybackEndedEventArgs)` — forward `PlaybackEnded?.Invoke(this, args)` event

## Why 2-partial decomposition works (1st observation)

- **Flow boundaries are stable**: file-IO lifecycle (`FileIoLifecycle`) + frame emission (`FrameEmission`) cleanly cluster with zero cross-flow entanglement.
- **Ctor delegates to `ReplayTimeline`**: The `ReplayService` ctor passes `EmitFrame` (from FrameEmission partial) as a delegate to `ReplayTimeline` ctor — partial-class cross-partial visibility handles this automatically (sister of W22+W23+W24+W25+W26+W27+W28+W29+W30 cross-partial helper pattern).
- **`[LoggerMessage]` partial stays on main**: 1 `[LoggerMessage]` partial declaration (`LogSinkThrew`) stays on `ReplayService` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31 sister precedent (CS8795 mitigation). Called from `EmitFrame` (in FrameEmission partial) — cross-partial call resolution handles this automatically.
- **Multi-interface preserved**: `ReplayService : IReplayService, IDisposable` declaration stays on main partial; both partials implement the interfaces transparently.
- **Cross-partial helper visibility works for 2 partials**: 8th confirmation (1st was W22, 2nd was W23, 3rd was W26, 4th was W27, 5th was W28, 6th was W29, 7th was W30, 8th is W31) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.

## Decision matrix (D1)

| Class shape | Pattern |
|---|---|
| Core/Replay subsystem god-class (file-IO lifecycle + frame emission) | **2-partial split** (FileIoLifecycle + FrameEmission) — W31 pattern |
| Core god-class with orchestration loop (timer tick + state machine) | Stays inline or different decomposition — see W22 + W23 stays |
| App/Services god-class with async file-load lifecycle | 2-partial split (LoadLifecycle/PersistenceOps + emission/mutator/text-decoding) — see `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` |
| App/Services god-class with JSON-persistence + lock-protected mutators + `%APPDATA%` path | 3-partial split (PersistenceFlow + Mutators + StaticHelpers) — see `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` |
| Infrastructure/Channel layer god-class | Different decomposition pattern — see `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` |
| Multi-interface partial god-class | Same flow-boundary pattern, NOT interface-boundary — see `multi-interface-partial-class-empirical-w26-w31-LOCKED` |

## Sister-precedent

- `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` — 3/3 CONFIRMED LOCKED at W31 (W31 ReplayService LoadAsync 31 LoC = 3rd confirmation of async file-load lifecycle pattern)
- `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` — 9/3 since 3/3 LOCKED at W25 (W31 ReplayService LoadAsync 31 LoC < 50 LoC → default D5 sister-principle per W29 NEW pattern)
- `small-god-class-no-largest-method-keeps-all-inline-default-pattern` — 1/3 → 2/3 PROMOTION at W31 (W31 is 2nd observation)
- `multi-interface-partial-class-empirical-w26-w31-LOCKED` — 3/3 CONFIRMED LOCKED at W31 (W31 ReplayService `IReplayService + IDisposable` = 3rd confirmation)
- `add-partial-keyword-to-monolithic-class-before-extraction` — 3/3 CONFIRMED at W26.5 (W31 already partial)

## Application scope

All future peakcan-host god-class refactors of Core/Replay subsystem classes (services that orchestrate the replay of recorded CAN frames, ASC files, BLF files, etc.). Awaiting 2 more observations across any future Core/Replay god-class to promote to 2/3 → 3/3 CONFIRMED → LOCKED into MASTER-LESSON-CATALOG.

## What to watch for in future refactors

- `ReplayViewModel.cs` (App/ViewModels — W16 sister, already extracted)
- Any new Core/Replay subsystem class added in future feature work
- Sister-extraction in other Core subsystems that adopt the file-IO lifecycle + emission flow-boundary shape

## Out of scope

- App/ViewModels (different layer, different concerns — see W21+W24+W25 sister precedent)
- App/Services stateful services with file-IO (W22 + W27 + W28 + W29 sister pattern — different decomposition shape)
- Infrastructure/Channel layer (W18 + W25 sister precedent — different decomposition pattern)
- Multi-interface partial classes (different concern — see `multi-interface-partial-class-empirical-w26-w31-LOCKED`)
