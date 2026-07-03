# v3.0.0 MINOR — Trace Viewer (offline playback) (2026-07-03)

## Summary

New non-modal `View ▸ Trace Viewer…` window that loads a recorded `.asc`
file and plays it back for **INSPECTION ONLY** — frames are **never**
written to the CAN bus. Sibling of the existing `Replay` (v1.4.0)
feature, which re-emits frames onto the bus. This MINOR distinguishes
**回放 (playback/inspection, this new feature)** from
**重发 (re-send, the existing Replay service)**.

The Trace Viewer reuses `ReplayTimeline` and `AscParser` (Core, internal)
under a **different** public interface — `ITraceViewerService` — that
has **no** `IReplayFrameSink` dependency. A reflection-based invariant
test enforces this constraint permanently.

```
AppShell.xaml ▸ Menu ▸ View ▸ Trace Viewer…  →  AppShellViewModel.ShowTraceViewerCommand
   →  new TraceViewerView(_traceViewerViewModel)  →  Window.Show()  (non-modal, Owner = main window)
       (horizontal split: left = signal list with Plot checkbox,
        right = per-signal OxyPlot.PlotView subplots, shared X-axis,
        playback ▶/⏸/⏹ + scrubber + speed + Loop + statistics chips + CSV export)
```

## Use cases

1. **Post-mortem analysis** — load a recording from a test drive, scrub
   the timeline, inspect what every CAN signal did over time.
2. **Live + recorded comparison** — keep `SignalView` running for live
   data; open `Trace Viewer` in a second window for historical reference.
3. **Failure investigation** — load `engine.asc` after a fault, scrub to
   the failure timestamp, see what every signal did in the seconds
   before.

## Architecture

- `ITraceViewerService` (new, Core, public interface) — mirrors
  `IReplayService` minus the `IReplayFrameSink` dependency.
  **No `SendAsync` reachable from this code path** — enforced by
  reflection in `TraceViewerServiceTests.Constructor_DoesNotAcceptIReplayFrameSink`
  (scans the assembly for the type and asserts it never appears as a
  ctor parameter, property, or field of `TraceViewerService`).
- `TraceViewerService` (new, Core, public sealed) — owns a private
  `ReplayTimeline` (internal, reused with zero modification) and
  `AscParser`. Each tick raises `FrameEmitted` to subscribers; never
  writes to any sink.
- `TraceViewerViewModel` (new, App) — orchestrates load + playback;
  bridges service events to `TraceChartViewModel` and `Signals`
  collection; exposes `OpenFileCommand`, `OpenDbcCommand`,
  `PlayCommand`, `PauseCommand`, `StopCommand`, `SeekCommand`,
  `ExportCsvCommand`.
- `TraceChartViewModel` (new, App) — owns
  `ObservableCollection<TraceChartSeries>`; each series has its own
  `OxyPlot.PlotModel`. Manages shared X-axis sync (via
  `LinearAxis.AxisChanged` events), playback cursor
  (`LineAnnotation`), statistics (min / max / avg / n), and CSV export.
- `TraceChartSeries` (new, App) — record `(SignalName, CanId, Unit,
  PlotModel, ColorIndex, IsEnabled)`; lightweight per-signal state.
- `TraceViewerView` (new, App) — non-modal `Window` with horizontal
  `GridSplitter` (left = signal list, right = chart subplots).
- DI: `AppHostBuilder` registers `ITraceViewerService → TraceViewerService`
  and `TraceViewerViewModel` as singletons (same assembly, no new
  project boundaries).

## UI features

| # | Feature | Notes |
|---|---------|-------|
| 1 | Open `.asc` | via `IFileDialogService` (asc/ASC filter) |
| 2 | Optional DBC load | decodes raw → signal name + physical value + unit |
| 3 | Horizontal split | left = signal list, right = chart subplots, splitter movable |
| 4 | Per-signal subplots | each `OxyPlot.PlotView` with own Y axis |
| 5 | Shared X axis (0..TotalDuration) | synced across all subplots via `AxisChanged` events |
| 6 | Playback controls | ▶ ⏸ ⏹ + scrubber + speed (0.25× / 0.5× / 1× / 2× / 4×) + Loop |
| 7 | Filter by CAN-ID + time range | (per spec §2; V1 stub) |
| 8 | Search by ID hex / signal name | (V1 stub) |
| 9 | Statistics chips | min / max / avg / n per charted signal |
| 10 | CSV export | all charted samples (X = time, one col per signal) |
| 11 | Subplot collapse / focus | (V1 stub — full impl in v3.0.1 if needed) |

## File cap

- Soft warn at 50,000 frames
- Hard cap at 100,000 frames

## Files added

- `src/PeakCan.Host.Core/Replay/ITraceViewerService.cs`
- `src/PeakCan.Host.Core/Replay/TraceViewerService.cs`
- `src/PeakCan.Host.App/ViewModels/TraceChartSeries.cs`
- `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs`
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml`
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs`
- `tests/PeakCan.Host.Core.Tests/Replay/TraceViewerServiceTests.cs`
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs`

## Files modified

- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (+DI registrations:
  `ITraceViewerService`, `TraceViewerService`, `TraceViewerViewModel`)
- `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`
  (+`ShowTraceViewerCommand` opens non-modal `TraceViewerView`)
- `src/PeakCan.Host.App/Views/AppShell.xaml`
  (+`View ▸ Trace Viewer…` menu entry after `Multi-frame...`)
- `tests/PeakCan.Host.App.Tests/Composition/AppHostBuilderTests.cs`
  (+2 DI registration tests)
- `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs`
  (+1 command test for `ShowTraceViewerCommand`)
- `docs/user-manual.html` (+new §12 Trace Viewer section; TOC updated)
- `docs/release-notes-v3.0.0.md` (this file)

## Test delta

| Suite | v2.1.7 | v3.0.0 | Δ |
|-------|--------|--------|---|
| Core | 387 | 392 | **+5** (`TraceViewerServiceTests`: 5 — `Load_Parses_Asc_File`, `FrameEmitted_Raises_For_Each_Tick`, `Seek_Jumps_To_Offset`, `Load_Throws_On_Malformed_File`, `Constructor_DoesNotAcceptIReplayFrameSink`) |
| App | 494 | 509 | **+15** (`TraceChartViewModelTests`: 7, `TraceViewerViewModelTests`: 5, `AppHostBuilderTests`: +2, `AppShellViewModelTests`: +1) |
| Infrastructure | 84 | 84 | 0 |
| **Total** | **965 + 6 SKIP** | **985 + 6 SKIP** | **+20 net** |

Race-flake counter preserved (30/30+).

## Lessons

Six lessons surfaced during Tasks 4-6 implementation and the Task 8
code review (all from the v3.0 pre-ship review):

1. **Reflection-based invariant test for "no bus sink"** — rather than
   trust the type system + code review, `Constructor_DoesNotAcceptIReplayFrameSink`
   scans the assembly at test time and fails if `IReplayFrameSink` ever
   appears as a ctor parameter / property / field of `TraceViewerService`.
   **Defends the design intent** (no bus writes) **against future
   regression** (someone adding a sink later "for convenience") at zero
   runtime cost. ~30 lines; reusable pattern for any "this type must
   never reference X" invariant.

2. **`ReplayTimeline.onSinkThrew` is a `null` callback slot, not an
   `Action<Exception>`** — when the timeline ticks a frame that the
   consumer can't deliver, the existing `IReplayService` passes a
   non-null logger; the new `TraceViewerService` passes `null` because
   there is no sink. The `null` is correct (the timeline guards against
   it) but the ctor signature uses a delegate type that doesn't
   communicate this. **Read the internal ReplayTimeline ctor before
   wiring a "no-sink" variant** — the pattern is `(_, _, null)`, not
   `(_, _, ex => log(ex))`.

3. **WPF `PlotModel.InvalidatePlot(false)` is the right knob for
   drag-scrubber responsiveness** — calling `InvalidatePlot()` (the
   parameterless overload) on every `ValueChanged` event of the X axis
   re-renders all 10k points in a chart and stalls the UI thread.
   `InvalidatePlot(false)` updates the annotation layer only
   (playback cursor + zoom box) and is ~50× faster. **Pass `false` for
   any "decorative" invalidation**; use the parameterless form only when
   series data has actually changed.

4. **`Post` vs `Send` matters for playback events** — `ReplayTimeline`
   exposes `FrameEmitted` via a `SynchronizationContext.Post`-style
   callback. If the consumer (the new VM) does heavy work on the UI
   thread synchronously, the timeline blocks. **Capture the current
   `SynchronizationContext` at ctor time and Post back, not Send**, so
   the timeline can keep ticking even when the UI is busy. This is the
   same pattern `IReplayService` uses; the lesson is "don't re-derive
   it — copy the existing call site."

5. **Shared X-axis sync via `Axis.AxisChanged` is re-entrant** — when
   one subplot's X axis changes, we propagate to all siblings. If the
   siblings then fire their own `AxisChanged` events, you get a loop
   where two subplots ping-pong updates. **Guard the sync with a
   `_syncing` boolean flag** that is set at the top of the handler and
   cleared at the bottom (try/finally); 5 lines, easy to forget
   without it.

6. **WPF STA-ctor + no-DI = hidden service injection** — `TraceViewerView`
   is a `Window` constructed in `AppShellViewModel.ShowTraceViewerCommand`
   via `new TraceViewerView(_traceViewerViewModel)`. The VM is reached
   via DI (singleton), but the `View` itself bypasses the container
   because WPF requires `Window` to be constructed on the STA thread.
   **Don't add service deps to the View directly** — wire them through
   the VM, and let the VM reach into DI. Same pattern as
   `MultiFrameSendWindow` (v2.1.0) and `ReplayWindow` (v2.1.4).

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| Subplot focus mode full impl | V1 stub only (button + click handler no-op); full impl in v3.0.1 if prioritized |
| Multi-trace comparison / diff | Out of V1 scope per spec §2 Non-goals |
| Bookmarks / annotations | Out of V1 scope per spec §2 Non-goals |
| PNG export | CSV only in V1 per spec §2 Non-goals |
| J1939 / CAN-XL multi-protocol decoding | Out of V1 scope per spec §2 Non-goals |
| Trace Viewer → Replay auto-load (long-term non-goal since v1.4.0) | Remains non-goal |
| DBC value-table encoding in Trace Viewer | Already non-goal in v1.4.0; consistent here |

## Process notes

- **Spec:** `docs/superpowers/specs/2026-07-03-trace-viewer-design.md`
  (commit `ca02d98`).
- **Plan:** 10 tasks / 13 h estimated (all shipped).
- **Pre-ship review:** Task 8 ran the full v3.0 surface through code
  review; 6 lessons captured above. No HIGH or CRITICAL findings at
  ship time.
- **Test isolation:** all Trace Viewer tests are STA-safe (no `[Fact]`
  on the UI thread); they run in the standard Core/App test
  pipelines and do not depend on the main window or DI host.
