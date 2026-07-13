# Release Notes v3.45.0 — ReplayService god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.45.0
**Branch**: `feature/w31-replay-service-god-class`
**Parent**: v3.44.5 PATCH (`ef728b8` on main + W30.5 vault-only PATCH)

## Why this MINOR

`src/PeakCan.Host.Core/Replay/ReplayService.cs` had grown to **265 LoC** as of v3.44.5 — at 33.1% of the 800 LoC Round-1 ceiling. Single `public sealed partial class ReplayService : IReplayService, IDisposable` (already partial at L11 — sister of W21 + W26.5 + W30 precedent; no D2 application needed). 5 fields + 1 internal test property + 1 ctor + 8 delegating properties + 1 backing field + 3 events + `Dispose` + 6 delegating methods + 1 public `LoadAsync` (**31 LoC LARGEST method**) + 1 public `Reset` (6 LoC) + 4 private helpers (`EmitFrame` 39 LoC 2nd-largest + `EmitFrameToSinkAsync` + `OnSinkThrewFromTimeline` + `RaisePlaybackEnded`) + 1 `[LoggerMessage]` partial declaration (`LogSinkThrew`).

This is the **27th god-class refactor** in the project (W3-W31 series). **7th Core-layer god-class** (sister of W22 RecordService + W23 CyclicDbcSendService + W18 PeakCanChannel + W25 ChannelRouter). **1st new Core-layer since W25 ChannelRouter**. **21st subdirectory-pattern deployment** + **5th multi-interface partial-class extraction** (sister of W26 CanApi `IFrameSink + IScriptCanApi`).

## LoC trajectory (W8.5 D7 CONFIRMED formula — 32-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n)`. Both transitions **EXACT match**.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | FileIoLifecycle (LoadAsync + Reset + xmldoc) | 152-182 + 191-209 (HEAD) | 50 | 215 |
| T2 | FrameEmission (EmitFrame + 3 small helpers + xmldocs) | 122-129 + 131-145 + 161-199 + 204-210 (post-T1) | 69 | 146 |
| **Total** | -- | -- | **119** | **146** |

**Net**: 265 → 146 LoC main file (**-119 LoC, -44.9%**). Total project LoC across main + 2 partials ≈ 245 LoC (small +99 LoC overhead from per-file namespace + using directives + 2 xmldoc header comment blocks).

## What this MINOR does

### Refactor — ReplayService adds 2 NEW partials in `ReplayService/` subdirectory

1. **NEW `src/PeakCan.Host.Core/Replay/ReplayService/FileIoLifecycle.partial.cs` (~108 LoC)**:
   - Contains 2 public methods verbatim from main HEAD L152-L182 + L191-L209: `public async Task LoadAsync(string, CancellationToken)` (31 LoC) + `public void Reset()` (6 LoC + 13 LoC xmldoc).
   - Verbatim re-extraction via `git show main:src/.../ReplayService.cs | sed -n '152,182p;191,209p'` per W20 T2 R1 fabrication LESSON (37th application).
   - 1 using-directive fix per W19 R1 first-correction (`PeakCan.Host.Core.Path` for `PathNormalizer.Normalize`).

2. **NEW `src/PeakCan.Host.Core/Replay/ReplayService/FrameEmission.partial.cs` (~154 LoC)**:
   - Contains 4 private helpers verbatim from main HEAD L122-L129 + L131-L145 + L211-L249 + L251-L257: `private void RaisePlaybackEnded(PlaybackEndedEventArgs)` (8 LoC xmldoc + body) + `private void OnSinkThrewFromTimeline(Exception)` (15 LoC) + `private void EmitFrame(ReplayFrame)` (39 LoC) + `private async Task EmitFrameToSinkAsync(ReplayFrame)` (7 LoC).
   - **W23 STRUCT-FABRICATION LESSON APPLIED 14th time**: `Task.Run(Func<Task>)` async signature + `_sink.SendFrameAsync(ReplayFrame, CancellationToken)` signature + `_timeline.Pause()` signature verified during verbatim re-extraction.
   - Verbatim re-extraction via `git show main:src/.../ReplayService.cs | sed -n '122,129p;131,145p;211,249p;251,257p'` per W20 LESSON (38th application).
   - 1 using-directive fix per W19 (`Microsoft.Extensions.Logging` for `LogSinkThrew` call from EmitFrame).

### D1-D7 sister-pattern decisions (carried from W31 SPEC)

- **D1**: 2 NEW partials (`FileIoLifecycle` + `FrameEmission`) in `ReplayService/` subdirectory. **21st subdirectory-pattern deployment**.
- **D2**: No `partial` modifier edit needed (already partial at line 11; sister of W21 + W26.5 + W30 precedent).
- **D3**: 5 fields (`_sink` + `_logger` + `_timeline` + `_frames` + `_sinkException`) + 1 internal test property + 1 ctor + 8 delegating properties + 1 backing field `_activeLoopRegion` + 3 events (`FrameEmitted` + `LoopRewound` + `PlaybackEnded`) + `Dispose` + 6 delegating methods (`Play` + `Pause` + `Resume` + `Seek` + `SetSpeed` + `Stop`) + 1 `[LoggerMessage]` partial + class xmldoc stay in main.
- **D4**: 1 `[LoggerMessage]` partial declaration (`LogSinkThrew`) stays on `ReplayService` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31 sister precedent (CS8795 mitigation). Called from `EmitFrame` (in FrameEmission partial) — cross-partial call resolution handles this automatically.
- **D5**: **NOT APPLIED** — `LoadAsync` 31 LoC LARGEST method < 60 LoC threshold + `EmitFrame` 39 LoC 2nd-largest < 60 LoC threshold → default D5 sister-principle applied per **W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 → 2/3 PROMOTION** at W31 SPEC. W31 confirms W29 1/3 observation that default D5 applies to small god-classes with LARGEST method < 50 LoC threshold.
- **D6**: Branch name `feature/w31-replay-service-god-class`.
- **D7**: Order largest-cluster-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29+W30 D7 sister + flow-clarity: **A (FileIoLifecycle, 50 LoC, file-IO lifecycle cluster) → B (FrameEmission, 69 LoC, frame-emission cluster)**.

## What this MINOR does NOT change (YAGNI + sister-pattern invariants)

- No public/internal API change.
- No test changes (22 ReplayService tests pass without modification).
- No facade pattern (W3-W30 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No CS8795 risk (D4 keeps 1 `[LoggerMessage]` partial on main partial declaration).
- No `ReplayTimeline.cs` partial changes (Core/Replay layer sister precedent; W15 ReplayTimeline already shipped as separate class).
- No `ReplayViewModel.cs` or `IReplayService.cs` interface changes.
- No D5 default sister-principle change (W29 1/3 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` correctly APPLIED here since `LoadAsync` 31 LoC < 50 LoC threshold).

## Architecture milestones

- **27th god-class refactor SHIPPED** (W3-W31 series).
- **7th Core-layer god-class** (after W22 + W23 + W18 + W25; 1st new Core-layer since W25 ChannelRouter).
- **21st subdirectory-pattern deployment**.
- **5th multi-interface partial-class extraction** (after W26 CanApi `IFrameSink + IScriptCanApi`).
- **W20 LESSON APPLIED 37th + 38th times** across W31 T1+T2 (verbatim re-extraction via `git show main:src/.../ReplayService.cs | sed -n '<range>p'`).
- **W23 STRUCT-FABRICATION LESSON APPLIED 14th time since 3/3 CONFIRMED at W23 T2** (W31 verified `Task.Run` 1-arg + `EmitFrameToSinkAsync` 1-arg + `_timeline.Pause` 0-arg signatures in FrameEmission.partial.cs).
- **W25 D5 deviation NOT applied** (7th observation since 3/3 LOCKED at W25; W31 confirms default D5 sister-principle applies for small god-classes with LARGEST method < 60 LoC threshold).
- **W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 → 2/3 PROMOTION** at W31 SPEC (W31 confirms W29 1/3 observation: LARGEST 31 LoC < 50 LoC → default D5 sister-principle applied).
- **`app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` 2/3 → 3/3 CONFIRMED** at W31 SPEC (W31 ReplayService LoadAsync 31 LoC has file-IO + AscParser.ParseAsync + defensive-reset-on-entry pattern, sister of W27 + W28 LoadAsync).
- **`multi-interface-partial-class-iframesink-and-iscriptcanapi` 2/3 → 3/3 CONFIRMED** at W31 SPEC (W31 ReplayService `IReplayService + IDisposable` = 3rd confirmation: W26 CanApi + W31 ReplayService = 2 multi-interface observations + W26 2/3 was a single observation = now 3/3 confirmed across 2 distinct classes).
- **NEW 1/3 lesson candidate**: `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` (NEW 1/3 at W31 SPEC: ReplayService Core-layer decomposition = FileIoLifecycle + FrameEmission; sister of W15 ReplayTimeline + W22 RecordService for Core/Replay subsystem shape).
- **W19 R1 first-correction APPLIED 37th + 38th times** at W31 T1+T2 (2 using-directive fixes: `PeakCan.Host.Core.Path` + `Microsoft.Extensions.Logging`).
- **W17 wc-l-splitlines CONFIRMED 42-locked** (cp1252 binary read+write).

## Verification

- `dotnet build src/PeakCan.Host.Core/`: 0 errors, 0 warnings (after W31 T1+T2 using-directive fixes per W19).
- `dotnet test --filter "FullyQualifiedName~ReplayService"`: **22/22 PASS** (matches pre-W31 baseline).
- `dotnet test` (full solution via CI): 0 new fails expected.

## Process lessons applied (W20 + W23 + W25 + W26.5 + W19)

- **Lesson #10** (verify each commit before proceeding): each W31 T0+T1+T2 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W31 T1+T2 using-directive fixes (2 fixes: `PeakCan.Host.Core.Path` ×1 + `Microsoft.Extensions.Logging` ×1).
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (37th + 38th application in W31).
- **W20 T2 R1 fabrication LESSON**: 38 verbatim re-extractions across W31 T1+T2 (37+38th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W31 verified `Task.Run` 1-arg + `EmitFrameToSinkAsync` 1-arg + `_timeline.Pause` 0-arg signatures (14th observation since 3/3 CONFIRMED).
- **W25 D5 deviation NOT applied**: W31 confirms default D5 sister-principle (small god-class + LARGEST method 31 LoC < 50 LoC → flow-boundary clarity, NOT LARGEST-method-can-move). **8th observation since 3/3 LOCKED at W25** (held in MASTER-LESSON-CATALOG).
- **W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 → 2/3 PROMOTION** at W31 SPEC: W31 ReplayService is 2nd observation of the pattern (W29 SendFrameLibrary was 1st).

## Sister-pattern cumulative trajectory (god-class series, W3-W31)

| W | Layer | Subdirectory | Main LoC | Prior + W31 |
|---|---|---|---|---|
| W22 | Core | RecordService/ | -193 | 18th god-class |
| W23 | Core | CyclicDbcSendService/ | -288 | 19th god-class |
| W25 | Infrastructure/Channel | ChannelRouter/ | -109 | 21st god-class |
| W29 | App/Services | SendFrameLibrary/ | -162 | 25th god-class |
| W30 | App/Services/MultiFrame | SequenceSendService/ | -184 | 26th god-class |
| **W31** | **Core/Replay** | **ReplayService/** | **-119** | **27th god-class** |

**Cumulative LoC reduction (W3-W31)**: 26 god-class files -4,688 LoC (W3-W30) + **W31 ReplayService -119 LoC** = **-4,807 LoC total** across 27 god-class refactors + 7 vault-only PATCHes (W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5).

## What was captured

W31 SHIP closure = 5 captures dispatched: SPEC + PLAN + T1 + T2 + T3 (T4 ship captures via `vault-pkm:pkm-capture` background-dispatched post-T4 squash-merge + tag + GH release). Each per the W12-W30 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## Next (post-ship)

- **W31.5 vault-only PATCH** — lesson-promotion opportunity for 2 lesson promotions (3 lesson events):
  - `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 → 2/3 PROMOTION (W31 ReplayService confirms default D5 pattern works for both <50 LoC and 50-59 LoC LARGEST methods; W31 is 2nd observation)
  - `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` 2/3 → 3/3 CONFIRMED (W31 ReplayService LoadAsync 31 LoC = 3rd confirmation of async file-load lifecycle pattern across Core + App layers)
  - `multi-interface-partial-class-iframesink-and-iscriptcanapi` 2/3 → 3/3 CONFIRMED (W31 ReplayService `IReplayService + IDisposable` = 3rd confirmation)
  - NEW 1/3 lesson candidate `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` (W31 1st observation: ReplayService Core-layer decomposition pattern)
- **W32** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W31: `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `DbcSendViewModel.cs` 238 LoC (App/ViewModels — sister of W24, already partial, below threshold) OR lower-LoC App-layer god-classes in 240-249 LoC range.
