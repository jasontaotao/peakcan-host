# W16 Spec — ReplayViewModel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` from 462 LoC to ~272 LoC by extracting 2 flow groups into sibling partial files (sibling-file pattern, NOT subdirectory — stays consistent with 4 existing `.partial.cs` files: Bookmarks/Loader/Bundle/Playback).

**Architecture:** Different from W3-W15 (which all used `Subdirectory/` pattern). W16 stays with the **sibling-file pattern** that's already established for `ReplayViewModel.*.partial.cs` (4 existing partials). Adds 2 NEW partials: `ReplayViewModel.RangeFilter.partial.cs` + `ReplayViewModel.PlaybackEvents.partial.cs`. Each partial declares `public sealed partial class ReplayViewModel`. Main file keeps: 11 readonly fields + 13+ ObservableProperty fields + 4 properties (IsNotLoaded + StartTimestamp + EndTimestamp + IsValidRange + RangeFilterError) — wait, 5 properties — + 2 nested records (`BookmarkVm` + `LoopRegionVm`) + 1 ctor. Each partial owns one logical flow group.

**Tech Stack:** C# .NET 10 + CommunityToolkit.Mvvm 8.x, App layer (WPF MVVM). The class is `public sealed partial class ReplayViewModel : ObservableObject, IDisposable` with `[ObservableProperty]` source-generated properties.

## Global Constraints

- **Public API unchanged.** No method signatures, properties, commands, events, or nested types move.
- **partial-class visibility.** All private methods + private fields visible across partial files.
- **Test coverage unchanged.** No tests added, removed, or modified. **No xmldoc-grep tests** for ReplayViewModel (verified per W12 D8 + W13 D8 sister).
- **Line-ending normalized to LF.**
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 3.** Tasks 1-2 keep `src/Directory.Build.props` at v3.30.0. Task 3 bumps to v3.31.0.
- **Branch**: `feature/w16-replay-view-model-god-class` (created from `main` @ `4afb5bc` v3.30.0).

---

## Current state (462 LoC main file; 4 existing partials 796 LoC)

`src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` (v3.30.0 HEAD) has **462 LoC**. Sister partials:
- `ReplayViewModel.Playback.partial.cs` (207 LoC) — partial IReplayService interaction layer
- `ReplayViewModel.Loader.partial.cs` (312 LoC) — partial session load/save
- `ReplayViewModel.Bookmarks.partial.cs` (102 LoC) — partial bookmarks panel
- `ReplayViewModel.Bundle.partial.cs` (175 LoC) — partial bundle save/load

Total class LoC = 462 + 796 = **1258 LoC across 5 files**. Main file = 462 / 1258 = 36.7%.

Threshold 800 LoC ceiling: main at 57.8% of ceiling. But the **class-shape inventory** (16+ `[ObservableProperty]` fields + 4 partial-class files + 2 nested records + ctor) signals god-class territory. Goal: reduce main below 350 LoC to "comfortable range".

## Target state (~272 LoC main + 5 partials)

```
src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs                   # main file, ~272 LoC after Task 2
src/PeakCan.Host.App/ViewModels/ReplayViewModel.RangeFilter.partial.cs     # NEW (Task 1) -- StartTimestamp + EndTimestamp + IsValidRange + RangeFilterError (~80 LoC)
src/PeakCan.Host.App/ViewModels/ReplayViewModel.PlaybackEvents.partial.cs   # NEW (Task 2) -- event handlers + Dispose (~110 LoC)
src/PeakCan.Host.App/ViewModels/ReplayViewModel.Playback.partial.cs          # EXISTING (no change)
src/PeakCan.Host.App/ViewModels/ReplayViewModel.Loader.partial.cs            # EXISTING (no change)
src/PeakCan.Host.App/ViewModels/ReplayViewModel.Bookmarks.partial.cs         # EXISTING (no change)
src/PeakCan.Host.App/ViewModels/ReplayViewModel.Bundle.partial.cs            # EXISTING (no change)
docs/superpowers/plans/2026-07-12-replay-view-model-god-class-refactor.md  # NEW (Task 0)
docs/release-notes-v3.31.0.md                                              # NEW (Task 3)
```

**Net reduction**: main 462 → ~272 (-190 LoC, -41.1%). Total class LoC unchanged (~1258 across main + 6 partials including NEW 2).

## Flow boundaries

### Flow — Main file (kept in `ReplayViewModel.cs`)

**Stays in main**:
- `using` block (lines 1-11) — 4 existing + new partial usings already imported per source
- Namespace + class xmldoc (lines 13-39)
- Outer class declaration (line 40) — already `public sealed partial class` since 4 prior partials exist, no change needed
- 11 readonly fields (lines 42-77) — state ownership for all 6 partials
- 13+ `[ObservableProperty]` fields (lines 79-162) — source-generator scope per W10+W11+W12+W13+W14+W15 sister-lesson
- `StartTimestamp` property (lines 171-194) + `EndTimestamp` property (lines 200-223) → **wait, these need to move to RangeFilter partial per D1**.
  
**Correction**: `StartTimestamp` + `EndTimestamp` + `IsValidRange` + `RangeFilterError` all move to Flow A.

**Main keeps**:
- usings + namespace + class xmldoc (1-39)
- 11 readonly fields (42-77)
- 13+ `[ObservableProperty]` fields (79-162)
- `IsNotLoaded` property (236) — convenience bool derived from `IsLoaded`
- Ctor (252-303)
- 2 nested records `BookmarkVm` (428-451) + `LoopRegionVm` (454-461) — sibling type declarations stay (per W9 D6 + W10 D5 sister)

### Flow A — RangeFilter (~80 LoC, NEW)

**Members**:
- `[ObservableProperty] private double? _startTimestamp;` backing field (line 163)
- `public double? StartTimestamp { get; set; }` property + xmldoc (lines 171-194) — inclusive lower bound on emitted frames' timestamp, range filter at OnTick iteration boundary, null = unbounded below
- `[ObservableProperty] private double? _endTimestamp;` backing field (line 194)
- `public double? EndTimestamp { get; set; }` property + xmldoc (lines 200-223) — inclusive upper bound, same composition + re-application semantics as `StartTimestamp`
- `private static bool IsValidRange(double? start, double? end)` (line 225) — validation helper for setter
- `[ObservableProperty] private string? _rangeFilterError;` backing field (line 233) — setter applies `IsValidRange` and updates `RangeFilterError`

**Depends on**:
- `[ObservableProperty]` source generator scope — backing fields + generated setters belong in this partial (CommunityToolkit.Mvvm convention)
- `_startTimestamp`, `_endTimestamp`, `_rangeFilterError` fields (partial-class visible from all 6 partials once split)

**Rationale for grouping**: RangeFilter is a tightly-coupled 4-property set (StartTimestamp + EndTimestamp + IsValidRange + RangeFilterError) — they're a single conceptual unit (range-filter UI section). Cross-references via `IsValidRange` private static method. Sister of W3 TraceViewerViewModel's Scrubber, W4 AppShellViewModel's SessionFlow, W6 SendViewModel's Send flow.

### Flow B — PlaybackEvents (~110 LoC, NEW)

**Methods**:
- `private void OnRecentSessionsPropertyChanged(object?, PropertyChangedEventArgs)` (lines 311-312)
- `private void OnFrameEmitted(ReplayFrame)` (lines 320-333) — marshals to `SynchronizationContext`
- `private void OnPlaybackEnded(object?, PlaybackEndedEventArgs)` (lines 342-354) — marshals to `SynchronizationContext`
- `private void ApplyPlaybackEnded(PlaybackEndedEventArgs)` (lines 356-364)
- `private void OnLoopRewound(object?, LoopRegionRewoundEventArgs)` (lines 375-387) — marshals to `SynchronizationContext`
- `public void Dispose()` (lines 407-417) — unsubscribes event handlers

**Depends on**:
- `_service`, `_recentSessions`, `_syncContext` (main fields)
- `CurrentTimestamp`, `IsPlaying`, `ErrorMessage`, `StatusMessage` (main `[ObservableProperty]` fields — setters generated by source generator)

**Rationale for grouping**: 5 event handlers + 1 unsubscribing disposal method all share `SynchronizationContext`-marshalling pattern (event arrives on timer thread, posted to UI thread). Plus `Dispose` unsubscribes the same handlers. **W14 D2 sister-lesson applied**: lifecycle primitives (event-handlers + Dispose) stay coupled in one partial. Logical sister of W14's ExecutionLifecycleFlow + W12's TransportFlow — both are "incoming-thread → UI-thread marshalling" clusters. Cross-partial visibility reaches into main fields for the unsubscriptions.

## Architecture invariants (per W3-W15 patterns)

1. **Public API unchanged.** `[ObservableProperty]`-generated public properties + xaml-bindable commands all stay callable with identical names + types.
2. **partial-class visibility** works for `[ObservableProperty]`-generated setters — the source generator emits setters in each partial file but they're observable across files via standard C# private field access.
3. **Sibling-file pattern preserved**: stays consistent with the 4 existing `.partial.cs` files (NOT moved to subdirectory). Renaming 4 existing partials to a subdirectory pattern would be a YAGNI churn per W10 R5.
4. **Nested records stay in main**: `BookmarkVm` + `LoopRegionVm` remain at end of main file per W9 D6 + W10 D5 + W12-W15 sister (type-declaration-with-class).
5. **State ownership preserved**: 11 readonly fields stay in main (per W11 D5 + W12 D5 + W13 D5 + W14 D5 + W15 D5 sister). 13+ `[ObservableProperty]` backing fields stay in main (CommunityToolkit.Mvvm source-generator convention — generator creates setters/getters in the file where the `[ObservableProperty]` attribute appears).

## Verification

- `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore`: 0 errors
- `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Replay"`: all tests pass without modification
- `dotnet test --no-restore --nologo -c Debug`: full solution builds clean, no regressions (1 transient flaky `Parse_MalformedLines_LogsEachWithLineNumberAndReason` expected per W13 T3 + W15 R-1 sister; isolated test passes)

## Risk notes

- **R1 (low)**: Missing `using` directives — per W3-W15 CONFIRMED lesson. Pre-scan. Likely 0 new usings (Flow A uses `[ObservableProperty]` + standard C#; Flow B uses `SynchronizationContext` already imported).
- **R2 (low)**: LoC formula — per W8.5 D7 CONFIRMED 13-locked. Use W13 T1 2/3 loose-assertion pattern (`splitlines(keepends=True)` off-by-one).
- **R3 (low)**: `[ObservableProperty]` source-generator behavior across partial files — **must verify**. The CommunityToolkit.Mvvm generator emits `On<Name>Changed` partial methods + property setters/getters. When the backing field `[ObservableProperty] private double? _startTimestamp;` is in one file and the partial method `OnStartTimestampChanged` is referenced from another partial, the source generator must handle the cross-file partial method. **Mitigation**: keep `[ObservableProperty]`-decorated backing fields + the corresponding `<Name>Changed` partial methods in the SAME partial file (the existing partials already do this; verify Flow A's `[ObservableProperty]` doesn't break this convention).
- **R4 (low)**: Sibling-file pattern vs subdirectory — staying with sibling-file pattern per D3. The 4 existing `ReplayViewModel.<X>.partial.cs` files don't use subdirectories. Adding 2 more sibling files is consistent.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W15 CONFIRMED direct partial-class visibility is sufficient.
- **No sub-class creation**: ReplayViewModel stays a single class.
- **No test changes**: All existing tests stay unmodified.
- **No public/internal API surface change**.
- **No subdirectory move** for the 4 existing partials — sibling-file pattern preserved.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A — `RangeFilter` (4 members: StartTimestamp + EndTimestamp + IsValidRange + `_rangeFilterError`).
2. **Task 2**: Extract Flow B — `PlaybackEvents` (5 event handlers + Dispose).
3. **Task 3**: Bump version v3.30.0 → v3.31.0 + write release notes (MINOR ship commit).
4. **Task 4**: Tier-3 push + tag + GH release.

Total: 4 tasks, ~3 source commits (1 per flow + 1 ship).

## Decision log

- **D1**: 2 partials (`RangeFilter` + `PlaybackEvents`) with names matching the existing `ReplayViewModel.<X>.partial.cs` sibling-file convention.
- **D2**: `[ObservableProperty]` backing fields stay in main BUT their public properties + `IsValidRange` + `_rangeFilterError` move to Flow A RangeFilter. Rationale: the `[ObservableProperty]` attribute must be on a field declaration (which can be in main) but the public property + setter logic + validation helper naturally belong with the conceptual range-filter unit. **[NEEDS VALIDATION]**: confirmed via reading existing partials — the `[ObservableProperty]` attribute lives on the field, and the source generator emits the property/change-handler as a separate declaration. We can split: field in main + property + setter + helper in partial. **Cross-validation in Task 1 execution**: confirm build succeeds with `[ObservableProperty]` on a field that's declared in main but used by property/handler in Flow A.
- **D3**: Branch name `feature/w16-replay-view-model-god-class`.
- **D4**: Order tasks: **A (RangeFilter) → B (PlaybackEvents)** — Flow A smaller (80 LoC, no threading); Flow B larger (110 LoC, threading-heavy). Smaller first validates `[ObservableProperty]`-across-partials pattern.
- **D5**: Plan LoC-trajectory-table formula explicitly applied per W8.5 D7 CONFIRMED (14-locked across W12-W16).
- **D6**: Dispose moves to Flow B because it's the symmetric-unsub for the event handlers that ARE in Flow B. Sister-lesson of W14 D2 (Dispose + ctors are paired with field-ownership OR with handler-ownership). Decision: Dispose stays with event handlers (Flow B), not with ctor (main).
- **D7**: W14 D8 sister-principle applies: a partial method moved across partial files should stay inline (verified in W12 D7 + W14 D8 + W15 D8). The 5 event handlers + Dispose are all small enough that inline extraction works.

## Closing milestone context

This is the **13th god-class refactor** in the project (W3-W16 series). ReplayViewModel is the **6th App layer** god-class (W3 + W4 + W5 + W6 + W7 + W8 + W11 + W14 + W16 = 9 App + 5 Core = 14; this makes 13 total). Actually let me recount:
- W3 TraceViewerViewModel (App)
- W4 AppShellViewModel (App)
- W5 SignalViewModel (App)
- W6 SendViewModel (App)
- W7 MultiFrameSendViewModel (App)
- W8 TraceChartViewModel (App)
- W9 IsoTpLayer (Core)
- W10 DbcParser (Core)
- W11 AppHostBuilder (App)
- W12 UdsClient (Core)
- W13 AscParser (Core)
- W14 ScriptEngine (App)
- W15 ReplayTimeline (Core)
- **W16 ReplayViewModel (App) — 13th god-class refactor, 7th App layer**

7th App layer god-class refactor. Sister of W3 (TraceViewerViewModel) + W8 (TraceChartViewModel) — same ViewModel pattern with `[ObservableProperty]` + `[RelayCommand]` source generators. **First W16 to use sibling-file pattern (NOT subdirectory)** because ReplayViewModel already has 4 partials in sibling-file convention — consistency over uniformity with W3-W15 subdirectory pattern.

If W16 ships + tests pass + lesson confirmations hold, W16.5 vault-only PATCH (lesson-promotion) and the next candidate (no remaining god-class refs in `>= 450 LoC` range after W3-W16) — the series might complete or shift to `>= 350 LoC` range targets.
