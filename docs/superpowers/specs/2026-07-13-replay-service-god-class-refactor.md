# W31 SPEC — ReplayService god-class refactor (27th overall, 7th Core-layer)

**Date**: 2026-07-13
**Target class**: `src/PeakCan.Host.Core/Replay/ReplayService.cs` (265 LoC)
**Target version**: v3.45.0 MINOR
**Sister pattern**: W22 RecordService + W23 CyclicDbcSendService + W18 PeakCanChannel + W25 ChannelRouter (4 Core-layer god-class refactors). **27th god-class refactor** in W3-W30 series.

## Context

`ReplayService` (265 LoC) is the **7th Core-layer god-class** candidate in the W3-W30 series (26 refactors shipped, 4 prior Core-layer sisters completed). The class orchestrates replay session control (file-IO + ASC parsing + timeline-driven frame emission + sink dispatch) for the Core layer.

**Class shape** (already verified via direct read):

- `public sealed partial class ReplayService : IReplayService, IDisposable` (L11) — **already partial** (W21 + W26.5 3/3 CONFIRMED + W30 T0-D2 sister precedent; no D2 needed)
- 5 readonly/private fields: `_sink` + `_logger` + `_timeline` + `_frames` + `_sinkException` (L13-L19)
- 1 internal test property: `SinkExceptionForTesting` (L20)
- 1 ctor (L22-L47, ~26 LoC)
- 8 delegating properties: `State` + `CurrentTimestamp` + `TotalDuration` + `Frames` + `Speed` + `Loop` + `StartTimestamp` + `EndTimestamp` + `ActiveLoopRegion` (L49-L110)
- 2 events: `FrameEmitted` + `LoopRewound` + `PlaybackEnded` (L63, L118, L120)
- `Dispose` 1 LoC (L150)
- 6 delegating methods: `Play` + `Pause` + `Resume` + `Seek` + `SetSpeed` + `Stop` (L184-L189, all 1 LoC each)
- 1 public method `LoadAsync(string path, CancellationToken ct = default)` returning `Task` — **31 LoC LARGEST method** (L152-L182)
- 1 public method `Reset` (L204-L209, 6 LoC)
- 4 private helpers:
  - `EmitFrame(ReplayFrame)` (L211-L249, 39 LoC)
  - `EmitFrameToSinkAsync(ReplayFrame)` (L254-L257, 4 LoC)
  - `OnSinkThrewFromTimeline(Exception)` (L137-L145, 9 LoC)
  - `RaisePlaybackEnded(PlaybackEndedEventArgs)` (L128-L129, 2 LoC)
- 1 `[LoggerMessage]` partial declaration: `LogSinkThrew` (L262-L264) — **STAYS ON MAIN per W18+W22+W23+W25+W26+W27+W28+W29+W30 sister precedent (CS8795 mitigation)**

**LARGEST method analysis** per W25 D5 deviation:

- `LoadAsync` 31 LoC < **60 LoC threshold** ✗
- `EmitFrame` 39 LoC < **60 LoC threshold** ✗
- **Both largest methods < 60 LoC** → W25 D5 deviation **NOT applicable**
- Per W12 + W14 + W18 + W19 + W20 + W21 + W22 + W23 + W29 D5 default sister-principle + **W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3**: **NO LARGEST-method-move deviation applied**. All methods stay inline OR extract per flow-boundary clarity.
- **W31 confirms the W29 1/3 observation**: LARGEST method 31 LoC (still < 50 LoC threshold) → default D5 sister-principle applies.

**Sister-extraction sequence** (Core-layer):

- W22 RecordService (2 partials: Lifecycle + Mutators)
- W23 CyclicDbcSendService (2 partials: TickLifecycle + CyclicOps)
- **W31 ReplayService (2 partials: FileIoLifecycle + FrameEmission)** — different decomposition shape (file-IO lifecycle + frame emission)

## W31 D1-D7

- **D1**: 2 NEW partials (`FileIoLifecycle` + `FrameEmission`) in `ReplayService/` subdirectory. **21st subdirectory-pattern deployment**.
- **D2**: No `partial` modifier edit needed (already partial at line 11; W21 + W26.5 3/3 CONFIRMED + W30 T0-D2 sister precedent).
- **D3**: 5 fields (`_sink` + `_logger` + `_timeline` + `_frames` + `_sinkException`) + 1 internal test property + 1 ctor + 8 delegating properties + 1 backing field `_activeLoopRegion` + 3 events + `Dispose` + 6 delegating methods + 1 `[LoggerMessage]` partial + class xmldoc stay in main.
- **D4**: 1 `[LoggerMessage]` partial declaration (`LogSinkThrew`) stays on `ReplayService` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30 sister precedent (CS8795 mitigation). Called from `EmitFrame` (in `FrameEmission` partial) — cross-partial call resolution handles this automatically.
- **D5**: **No D5 deviation** — `LoadAsync` 31 LoC LARGEST method < 60 LoC threshold + `EmitFrame` 39 LoC 2nd-largest < 60 LoC threshold → default D5 sister-principle applied (W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 → 2/3 promotion at W31 SPEC).
- **D6**: Branch name `feature/w31-replay-service-god-class`.
- **D7**: Order largest-cluster-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29+W30 D7 sister + flow-clarity: **A (FileIoLifecycle, 50 LoC, file-IO lifecycle cluster) → B (FrameEmission, 69 LoC, frame-emission cluster)**. A first (LoadAsync 31 LoC LARGEST method); B second (EmitFrame 39 LoC + 3 small helpers).

## Architecture

Sister pattern of W22 + W23 + W27 + W28 + W29 + W30 (subdirectory + non-suffix `.partial.cs` filenames). 27th god-class refactor. **7th Core-layer god-class** + **21st subdirectory-pattern deployment** + **5th multi-interface partial-class extraction** (sister of W26 CanApi `IFrameSink + IScriptCanApi`).

### Flow boundaries (Phase 1 verified)

**Stays in main (~146 LoC)**:
- `using` block (L1-L2) + namespace (L4) + class xmldoc (L6-L10)
- `public sealed partial class ReplayService : IReplayService, IDisposable` (L11)
- 5 fields: `_sink` + `_logger` + `_timeline` + `_frames` + `_sinkException` (L13-L19)
- 1 internal test property: `SinkExceptionForTesting` (L20)
- 1 ctor (L22-L47)
- 8 delegating properties: `State` (L49-L51) + `CurrentTimestamp` (L52) + `TotalDuration` (L53) + `Frames` (L60) + `Speed` (L62) + `Loop` (L65-L69) + `StartTimestamp` (L77-L81) + `EndTimestamp` (L87-L91) + `ActiveLoopRegion` (L106-L110)
- 1 backing field: `_activeLoopRegion` (L97)
- 3 events: `FrameEmitted` (L63) + `LoopRewound` (L118) + `PlaybackEnded` (L120)
- `Dispose` (L150, 1 LoC)
- 6 delegating methods: `Play` + `Pause` + `Resume` + `Seek` + `SetSpeed` + `Stop` (L184-L189, 1 LoC each)
- 1 `[LoggerMessage]` partial declaration: `LogSinkThrew` (L262-L264)

**Flow A — FileIoLifecycle (~50 LoC, T1) → `ReplayService/FileIoLifecycle.partial.cs`**:

- 1 public method `LoadAsync` (L152-L182, **31 LoC, LARGEST method, stays inline per default D5**)
- 1 public method `Reset` (L191-L209, xmldoc + body = 19 LoC)

Touches: `_frames` + `_timeline` (delegate to `ReplayTimeline.SetFrames` + `ReplayTimeline.Stop`).

**Flow B — FrameEmission (~69 LoC, T2) → `ReplayService/FrameEmission.partial.cs`**:

- 1 private helper `EmitFrame(ReplayFrame)` (L211-L249, 39 LoC, **2nd-largest method, stays inline per default D5**)
- 1 private helper `EmitFrameToSinkAsync(ReplayFrame)` (L251-L257, comment + body = 7 LoC)
- 1 private helper `OnSinkThrewFromTimeline(Exception)` (L131-L145, xmldoc + body = 15 LoC)
- 1 private helper `RaisePlaybackEnded(PlaybackEndedEventArgs)` (L122-L129, xmldoc + body = 8 LoC)

Touches: `CanIdFilter` + `_sink` + `_logger` + `_timeline` + `_sinkException` + `FrameEmitted` + `PlaybackEnded`.

**Cross-partial caller pattern**: ctor (stays in main) passes `EmitFrame` (Flow B partial) as a delegate to `ReplayTimeline` ctor — partial-class cross-partial visibility handles this automatically (sister of W22+W23+W24+W25+W26+W27+W28+W29+W30 cross-partial helper pattern).

## LoC trajectory (W8.5 D7 32-locked + W19 R1 first-correction + W17 wc-l-splitlines CONFIRMED + W20 fabrication LESSON APPLIED 36+ times in W30 + W23 STRUCT-FABRICATION LESSON APPLIED 13 times)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — FileIoLifecycle | L152-L182 (LoadAsync) + L191-L209 (Reset) = 50 LoC | 50 | 1 | 215 |
| T2 | B — FrameEmission | L122-L129 (RaisePlaybackEnded) + L131-L145 (OnSinkThrewFromTimeline) + L211-L249 (EmitFrame) + L251-L257 (EmitFrameToSinkAsync) = 69 LoC | 69 | 1 | 146 |
| T3 | v3.44.5 -> v3.45.0 | (no source) | 0 | 0 | 146 |
| T4 | ship | -- | -- | -- | 146 |

Cumulative: 265 -> 215 -> 146 main. **Re-grep + range verify after each task per W19 R1 + W17 CONFIRMED**.

## W20 + W23 LESSON APPLIED — verbatim re-extract + struct-ctor verification

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle struct-constructor fabrication, W31 will:

1. **Re-grep boundaries BEFORE running each deletion script** (Phase 1 done above; re-verify before T1 + T2 script runs).
2. **Re-extract original code from main HEAD via `git show main:src/.../ReplayService.cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **CRITICAL: Verify `EmitFrameToSinkAsync` `_sink.SendFrameAsync` signature** + `EmitFrame` `Task.Run` async pattern + `OnSinkThrewFromTimeline` `_timeline.Pause()` signature — W23 LESSON applied (sister of W29 SaveUnlocked 4-line struct-fabrication verification).
4. **Verify `[LoggerMessage]` partial declaration stays on main** — D4 sister-pattern.
5. **Build + run filter tests after each task** to catch any extraction errors immediately.

## Tasks

### T0: Branch + SPEC + PLAN commits

```bash
git checkout -b feature/w31-replay-service-god-class main
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ReplayService" --logger "console;verbosity=minimal"
git add docs/superpowers/specs/2026-07-13-replay-service-god-class-refactor.md
git commit -m "W31 spec: ReplayService god-class refactor (2 partials + 5-task roll-out, 27th overall, 7th Core-layer, 1st new Core-layer since W25 ChannelRouter, 21st subdirectory-pattern deployment)"
git add docs/superpowers/plans/2026-07-13-replay-service-god-class-refactor.md
git commit -m "W31 plan: ReplayService god-class refactor (2 partials: FileIoLifecycle + FrameEmission)"
```

### T1: FileIoLifecycle partial (~50 LoC)

Write `scripts/w31_task1_delete_fileiolifecycle.py` with W19 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern. Range: L152-L182 (LoadAsync) + L191-L209 (Reset). Expected: 265 - 50 + 1 ≈ 215 LoC. Build + tests, commit.

### T2: FrameEmission partial (~69 LoC)

Re-grep post-T1 ranges. Write `scripts/w31_task2_delete_frameemission.py`. Range: L122-L129 + L131-L145 + L211-L249 + L251-L257. Expected: 215 - 69 + 1 ≈ 146 LoC. Build + tests, commit.

### T3: v3.44.5 → v3.45.0 MINOR + release notes

Mirror W30 release notes format. MINOR (2 NEW partial extractions = architectural change).

### T4: Tier-3 ship

`gh pr create` → `--squash --delete-branch` → `git tag v3.45.0` → `gh release create`.

## Sister-lesson candidates to monitor

| Lesson | Status | What W31 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W31 6th god-class application (T1+T2) — 37th application total |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30 13-of-1) | W31 14th observation (`Task.Run` async signature + `_sink.SendFrameAsync` signature verification applied) |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30 9-of-1) | W31 10th confirmation (1 `[LoggerMessage]` partial on main + called from `EmitFrame` in FrameEmission partial) |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | W31 already partial (no D2 application) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | 7/3 since 3/3 LOCKED (W30) | W31 8th observation (default D5 applied since LARGEST 31 LoC < 60 LoC; no move) |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | W31 21st deployment, sister-of-W30 |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` | 3/3 CONFIRMED LOCKED (W29.5) | N/A — W31 has file-IO lifecycle (ASC parsing), NOT JSON persistence |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` | 2/3 (W28.5) | **2/3 → 3/3 CONFIRMED at W31 SPEC**: W31 ReplayService LoadAsync 31 LoC has file-IO + AscParser.ParseAsync + defensive-reset-on-entry pattern, sister of W27 LoadAsync + W28 LoadAsync (3 confirmations across Core+App layers) |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | NEW 1/3 (W29) | **1/3 → 2/3 PROMOTION at W31 SPEC**: W31 ReplayService LARGEST method 31 LoC < 50 LoC threshold → default D5 sister-principle applied (no W25 D5 deviation). W31 confirms W29 1/3 observation that default D5 applies to small god-classes with LARGEST method <50 LoC. Awaiting 1 more observation to promote to 3/3 CONFIRMED → LOCKED |
| `app-services-multiframe-layer-sister-pattern-empirical-w30` | NEW 1/3 (W30) | N/A — W31 is Core/Replay, NOT App/Services/MultiFrame |
| `multi-interface-partial-class-iframesink-and-iscriptcanapi` | 2/3 (W26.5) | **2/3 → 3/3 CONFIRMED at W31 SPEC**: W31 ReplayService `IReplayService + IDisposable` = 2nd multi-interface observation after W26 CanApi `IFrameSink + IScriptCanApi` (2 confirmations: W26 + W31) |
| `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` | **NEW W31 1/3** | W31 1st observation: ReplayService Core-layer decomposition (FileIoLifecycle + FrameEmission) = 2-partial pattern for replay-state + file-IO; sister of W15 ReplayTimeline (different decomposition shape — W15 was ReplayTimeline itself, W31 is the service that owns the timeline) |

## Verification

- `dotnet build src/PeakCan.Host.Core/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~ReplayService"`: tests pass without modification
- `dotnet test` (full solution): 0 new fails
- `wc -l src/PeakCan.Host.Core/Replay/ReplayService.cs` ≤ 160 LoC (target ~146)
- 2 NEW partial files in `ReplayService/` directory
- 5 fields + 1 internal test property + 1 ctor + 8 delegating properties + 1 backing field + 3 events + `Dispose` + 6 delegating methods + 1 `[LoggerMessage]` partial remain in main
- DI registration unchanged (production DI binds `AddSingleton<IReplayService, ReplayService>(...)`)
- Public API unchanged (`IReplayService` interface + `IDisposable` interface)
- Tag v3.45.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes.
- No facade pattern (W3-W30 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No W18 R1 mitigation needed (D4 keeps all 1 `[LoggerMessage]` partials on main partial declaration; CS8795 mitigation via cross-partial visibility).
- No `ReplayTimeline.cs` partial changes (Core/Replay layer sister precedent; W15 ReplayTimeline already shipped as separate class).
- No `ReplayViewModel.cs` or `IReplayService.cs` interface changes.
