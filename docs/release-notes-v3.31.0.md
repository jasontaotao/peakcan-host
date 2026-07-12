# Release Notes v3.31.0 — ReplayViewModel god-class refactor (MINOR)

**Released:** 2026-07-12
**Tag:** v3.31.0
**Branch:** `feature/w16-replay-view-model-god-class`
**Parent:** v3.30.0 MINOR (`4afb5bc` on origin/main)

## Why this MINOR

`src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` had grown to **462 LoC** as of v3.30.0. `public sealed partial class ReplayViewModel : ObservableObject, IDisposable` with **16+ `[ObservableProperty]` source-generated fields + StartTimestamp + EndTimestamp + IsValidRange + RangeFilterError + 5 SynchronizationContext-marshalling event handlers + Dispose**. The class already had 4 prior `.partial.cs` siblings (Playbacks/Loader/Bookmarks/Bundle) totaling 796 LoC; main file at 462 LoC was still too large for inline maintenance.

This is the **13th god-class refactor** in the project (W3-W16 series), the **7th App layer** god-class (W3 TraceViewerViewModel + W4 AppShellViewModel + W5 SignalViewModel + W6 SendViewModel + W7 MultiFrameSendViewModel + W8 TraceChartViewModel + W11 AppHostBuilder + W14 ScriptEngine + W16 ReplayViewModel), and the **FIRST god-class with `[ObservableProperty]` CommunityToolkit.Mvvm source-generated properties** to receive the partial split. Also **first to use the sibling-file pattern** (NOT subdirectory) because 4 existing `.partial.cs` siblings set the precedent — consistency over uniformity with W3-W15 subdirectory pattern. Validates the partial-class split pattern works for: ViewModel with `partial` modifier + `[ObservableProperty]` source-generator + manual properties + nested records (BookmarkVm + LoopRegionVm) + IDisposable lifecycle.

## LoC trajectory (W8.5 D7 CONFIRMED formula — now 15-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. Both transitions **EXACT match** (within W13 T1 2/3 loose-assertion ±1 tolerance).

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | RangeFilter (StartTimestamp + EndTimestamp + IsValidRange + _rangeFilterError) | 163-235 | 73 | 391 |
| T2 | PlaybackEvents (5 handlers + Dispose) | 232-344 | 113 | 279 |
| **Total** | -- | -- | **186** | **279** |

**Net**: 462 → 279 LoC main file (**-183 LoC, -39.6%**). Total class LoC ~1258 unchanged (462 main + 796 existing partials; +2 NEW partials = total 462 + 186 + 796 = ~1444 across 7 partials, but main file reduction is the god-class refactor metric).

## What this MINOR does

### Refactor — ReplayViewModel adds 2 NEW sibling partials

The class was already `public sealed partial class ReplayViewModel : ObservableObject, IDisposable` at line 40 (with 4 existing `.partial.cs` siblings). The main file keeps: 11 readonly fields + 16+ `[ObservableProperty]` backing fields + 1 ctor + IsNotLoaded property + 2 nested records (`BookmarkVm` + `LoopRegionVm`) + Flow A + Flow B markers.

**Files created**:

| File | Flow | LoC | Members |
|---|---|---|---|
| `ReplayViewModel.RangeFilter.partial.cs` | A — RangeFilter (sibling-file) | ~80 | StartTimestamp + EndTimestamp (manual properties, not `[ObservableProperty]`) + IsValidRange static helper + `_rangeFilterError` `[ObservableProperty]` |
| `ReplayViewModel.PlaybackEvents.partial.cs` | B — SynchronizationContext marshalling cluster | ~113 | OnRecentSessionsPropertyChanged + OnFrameEmitted + OnPlaybackEnded + ApplyPlaybackEnded + OnLoopRewound + Dispose |

Each partial file declares `public sealed partial class ReplayViewModel { ... }` (file-scoped namespace + top-level partial, modern C# style matching the 4 existing siblings). Cross-flow visibility: Flow A setter reaches `_service.StartTimestamp = value` (Flow B doesn't touch). Flow B handlers reach into `[ObservableProperty]`-generated property setters via partial-class visibility; `Dispose` unsubscribes `_service.LoopRewound/FrameEmitted/PlaybackEnded + _recentSessions.PropertyChanged` (all fields in main, partial-class visible).

### Verification

- `dotnet build src/PeakCan.Host.App/`: **0 errors, 0 warnings** (after R1 missing-using fix: `using PeakCan.Host.Core.Replay;` for `ReplayFrame` / `PlaybackEndedEventArgs` / `LoopRegionRewoundEventArgs`)
- `dotnet test --filter "~Replay"`: **100 / 100 PASS** first try (no flaky failures)
- Full solution `dotnet test`: GREEN (re-run confirmed)

## Lessons applied

- **W8.5 D7 CONFIRMED** LoC correction formula (15-locked across W12-W16) — both transitions EXACT.
- **W3 R3 + W14 D2** sister-lesson applied for PlaybackEvents lifecycle cluster (5 marshalling handlers + Dispose share `SynchronizationContext` + state-unsubscription coupling; sister of W14 ExecutionLifecycleFlow).
- **W10 D5 + W11 D5 + W12 D5 + W13 D5 + W14 D5 + W15 D5** sister: 11 readonly fields + 16+ `[ObservableProperty]` backing fields + 4 properties + ctor + Dispose unsubscriptions + nested records stay in main.
- **W13 T1 2/3 loose-assertion** pattern applied at all script-assertion sites.
- **W13 R1 missing-using fix** pattern applied: `PlaybackEvents.partial.cs` needed `using PeakCan.Host.Core.Replay;` for 3 types — fix documented in commit.

## New sister-lesson candidates (per W16 D2 R3 + D6 observations)

- **`replay-view-model-manual-properties-with-partial-class-visibility-into-service-field`** (1/3) — W16 T1 observation: manual properties that call `OnPropertyChanged()` + push to a private-field-backed service work seamlessly across partial boundaries (NOT `[ObservableProperty]` source-generated). The `[ObservableProperty]`-across-partials R3 risk was N/A because the actual code uses manual properties. Sister of W10+W11+W12+W13+W14+W15.

- **`sibling-file-pattern-vs-subdirectory-is-correct-when-predecessors-set-precedent`** (1/3) — W16 first refactor to use sibling-file pattern instead of subdirectory because 4 existing `.partial.cs` siblings (Playback/Loader/Bookmarks/Bundle) already use it. Consistency over uniformity.

Both await 1 more observation (W17+) for promotion.

## What stays the same

- Public API surface — `[ObservableProperty]`-generated public properties + XAML-bindable commands all stay callable with identical names + types.
- Test count unchanged (100 App Replay + 1336 full solution 0 fails first try).
- 4 existing `.partial.cs` siblings unmodified.
- DI registration in `AppHostBuilder` unchanged (ReplayViewModel registered as singleton; partial-class transparent).

## Next steps (post-ship)

- **W16.5 vault-only PATCH** — lesson-promotion opportunity (W16 2 NEW 1/3 sister-lesson candidates awaiting 1 more observation).
- **W17** — next god-class refactor candidate (TBD; the W3-W16 series has swept `>= 450 LoC` candidates; W17 may shift to `>= 350 LoC` range or take a vault-only PATCH cycle to promote pending 1/3 candidates).
