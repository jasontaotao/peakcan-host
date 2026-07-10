# Release Notes v3.17.0 — TraceViewerViewModel god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.17.0
**Branch:** `v3-16-9-x-patch-chain`
**Parent:** v3.16.9.5 PATCH (`a96f3ce6` on origin/main)

## Why this MINOR

`src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` had grown to
**1934 LoC** as of v3.16.9.5 — too large to hold in context for editing,
review, or diff. The single sealed partial class owned six logically
distinct responsibilities stacked end-to-end:

| Flow | Responsibility | Methods | ~LoC |
|---|---|---|---|
| A | Source management (registry add/remove, master swap, DBC auto-rebuild) | 7 | ~150 |
| B | Transport playback (Play/Pause/Stop/Seek + scrubber/loop/speed) | 7 | ~90 |
| C | Signal table + filter (rebuild, frame count refresh, signal rows) | 4 | ~140 |
| D | Watch list + chart plotting (Add/Remove/Toggle plot + Plot helpers) | 9 | ~280 |
| E | Session save/load + bundled restore (build/apply snapshot) | 6 | ~380 |
| F | Lifecycle + frame pump (attach/detach handlers, master playback end) | 4 | ~80 |

The 800 LoC Round-1 ceiling from `automotive-coding-standards-file-size.md`
was exceeded by **2.4×**.

## What this MINOR does

### Refactor — TraceViewerViewModel split into 6 partial-class files

The god-class is split into 6 partial files in the same namespace, each
holding one logical flow. The main `TraceViewerViewModel.cs` keeps the
constructor, public state properties, [RelayCommand] entry points, and
Dispose.

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `TraceViewerViewModel/SourceFlow.cs` | A | 303 | AddTraceAsync, RemoveTraceAsync, CanAddTrace, SetMaster, OnRegistrySourcesChanged, RemoveOrphanChartSeries, OnDbcLoaded + 4 log helpers |
| `TraceViewerViewModel/TransportFlow.cs` | B | 97 | Play, Pause, Stop, SeekTo + OnScrubberValueChanged, OnLoopChanged, OnSpeedChanged |
| `TraceViewerViewModel/SignalFlow.cs` | C | 215 | OnCanIdFilterChanged, RebuildSignalsCore, RefreshFrameCounts, BucketFramesByCanId, BuildSignalRowsFromDbcOnly |
| `TraceViewerViewModel/WatchFlow.cs` | D | 374 | TogglePlot, SetPlotOptIn (2 overloads), AddToWatch + AddToWatchForPicker, FinalizePickerAdds, RemoveFromWatch, EnsurePlaceholderRow, PlotSignalFromTableRow, UnplotSignalFromTableRow |
| `TraceViewerViewModel/SessionFlow.cs` | E | 303 | SaveSessionAsync, OpenSessionAsync, BuildSnapshot + BuildSnapshotAsync, LogHashFailed, ApplySnapshotAsync |
| `TraceViewerViewModel/LifecycleFlow.cs` | F | 79 | AttachAllServiceHandlers, DetachAllServiceHandlers, OnMasterPlaybackEnded, OnAnyFrameEmitted |

**Main file** `TraceViewerViewModel.cs`: **1934 → 686 LoC (-1248 LoC, -64.5%)** — now small enough to read in one screen, well under the 800 LoC ceiling.

### Architecture invariants preserved

- **Public API unchanged**: all public method signatures, [RelayCommand] attributes, and [ObservableProperty] backing fields stay in the main file. XAML bindings are not affected.
- **partial-class visibility**: private methods are visible across partial files; cross-flow calls stay as plain method invocations (no method renaming required, no interface extraction).
- **State and DI**: all state fields (`_registry`, `_dbcService`, `_masterService`, `_allServices`, etc.) and DI-injected services stay in the main file. The partial files consume them transparently.

## What this MINOR does NOT do

- **No behavioral change.** Zero test changes, zero production-fix changes. The refactor is a pure mechanical split — every method body, xmldoc, and comment moved verbatim.
- **No API surface change.** No new public methods, no removed methods, no renamed members.
- **No state ownership change.** All mutable state still lives on the singleton partial class.

## Verification

- **dotnet build (Debug, warn-as-error)**: 0 errors. The single pre-existing `CS8602` nullable warning in `DbcService.cs:157` is unrelated to this refactor.
- **dotnet test --filter TraceViewerViewModel**: **79/79 PASS, 0 fail, 0 skip**. All tests pass unmodified — the partial-class split is transparent at runtime.
- **Pre-existing parallel-runner flakes** (unrelated): `IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` and `AscParserTests.Parse_MalformedLines_LogsEachWithLineNumberAndReason` fail under the parallel runner but pass in isolation. Same status as v3.16.9.5.
- **Main file LoC reduction**: 1934 → 686 LoC (-1248 LoC, **-64.5%**). Cumulative W3: 6 source commits + 1 scripts commit + 2 docs commits on `v3-16-9-x-patch-chain`.

## Risk notes

- **R1 (mitigated)**: The deletion script for Task 3 (Flow A) initially used regex matching and accidentally removed the `namespace` declaration along with `OnRegistrySourcesChanged`. Caught on first compile attempt; fixed by switching to line-range slicing (read file as `string[]`, delete by `(start, end)` tuples, write back). All subsequent tasks (4-6) used the safer pattern.
- **R2 (mitigated)**: Missing `using` directives caused 2 transient build failures (Task 4 missing `Microsoft.Extensions.Logging` for `_logger.LogInformation`, Task 6 missing `System.IO` + wrong `PeakCan.Host.Core.Replay.Session` namespace). Fixed on first retry each time. Documented as a new 1-of-1 lesson.
- **R3 (YAGNI applied)**: `EnsurePlaceholderRow` was originally scoped to Flow D only; in fact it's used by Flow A (`OnRegistrySourcesChanged`) and Flow C (`RebuildSignalsCore`) too. Kept in Flow D as the "writer" of the row, with cross-flow callers via partial-class visibility — the alternative (splitting into 3 separate placeholder helpers per flow) would create triplicate logic.

## Files in this ship

### Source code changes (6 commits)

```
d4cd7e3 refactor(tvvm): extract Flow E (session save/load + bundled restore) to partial class
e3d1903 refactor(tvvm): extract Flow D (watch list + chart plotting) to partial class
1347fbf refactor(tvvm): extract Flow B (transport playback) to partial class
f6f5908 refactor(tvvm): extract Flow A (source management + master swap) to partial class
da24d47 refactor(tvvm): extract Flow F (lifecycle + frame pump) to partial class
3ea11e1 refactor(tvvm): extract Flow C (signal table + filter) to partial class
```

### Scripts (1 commit)

```
e2c0fb4 chore(scripts): add W3 Task 1 + Task 2 deletion helper scripts
```

### Docs (2 commits)

```
89b5347 docs(plan): TraceViewerViewModel god-class refactor — 7-task execution plan
02259bb docs(spec): TraceViewerViewModel god-class refactor design (W3 brainstorm output)
```

## For the next session

- All 6 flows are now extracted; the god-class is gone.
- W3 plan is fully executed (7 of 7 tasks done — including this ship).
- The `v3-16-9-x-patch-chain` branch is the W3 MINOR branch; consider whether to merge back into `main` or keep as a long-lived feature branch.
- 19 NEW 1-of-1 lessons captured across the W3 work block (2 promoted to standalone: `deletion-script-must-preserve-namespace-and-using-clauses-when-removing-methods` + `partial-class-extraction-requires-duplicate-using-imports-when-methods-use-extension-methods`).