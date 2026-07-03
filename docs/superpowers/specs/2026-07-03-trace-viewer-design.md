# Trace Viewer Design

**Date:** 2026-07-03
**Status:** Draft — pending user review
**Branch:** `feature/v3-0-trace-viewer` (TBD; not yet created)

## 1. Context

peakcan-host currently has a **Replay** service (v1.4.0 MINOR onward) that loads an ASC trace file and **re-emits every frame onto the CAN bus** via `IReplayFrameSink.SendFrameAsync`. The user correctly identifies this as **重发 (re-send)**: it reconstructs original bus traffic on a live bus, modifying real bus state.

The **回放 (playback)** use case — loading a recording and inspecting what happened, **without writing to the bus** — is not currently served. The existing `SignalView` shows a real-time 30-second-rolling OxyPlot chart of currently-broadcast signals, which is closer to "回放" in spirit but is live-only (it cannot load historical trace data).

**Goal:** A new non-modal `Trace Viewer` window that loads an ASC trace, decodes frames against an optional DBC, and shows the recorded signal values over time as a chart — purely for inspection. It must never write to the CAN bus, even by accident.

## 2. Goals & non-goals

### Goals (V1)

- Open `.asc` files and parse them via the existing `AscParser`.
- Optionally load a DBC to decode raw frame data into named signals + physical engineering values.
- Render each charted signal as its own `OxyPlot.PlotView` with **independent Y-axis**, stacked vertically, sharing the X-axis (recording time, 0..TotalDuration).
- **Zoom / pan / measure** on the charts: rectangle zoom, mouse-wheel X pan, vertical measurement cursors with `Δt` and `Δvalue` readouts.
- **Playback cursor** (red vertical line) tracks the current playback position; sync with playback ▶ and the time scrubber.
- **Horizontal split layout** — left: signal list (one row per `(CAN ID, signal)` pair with Plot checkbox, ID, signal name, latest value, unit); right: chart subplots. Splitter is user-movable.
- **Filter** by CAN-ID and time range.
- **Search** by CAN-ID hex / decimal or signal name.
- **Statistics** chips (min / max / avg / n) per charted signal.
- **CSV export** of all charted samples (X = time, one column per signal).
- **Subplot focus mode** — click a subplot header to expand it to 50% of vertical real estate, others collapse to share the rest.
- File size cap: 50,000 frames (soft warning above; hard cap at 100,000 frames).

### Non-goals (V1)

- **No CAN bus writes** — no `IReplayFrameSink` involvement at any layer; no "Send" / "Replay" button; no API surface that could send.
- No frame construction / DBC editing (covered by `MultiFrameSendWindow`).
- No live signal subscription (covered by `SignalView`).
- No DBC hot-reload (load once per session; reload requires window restart).
- No multi-DBC merge.
- No bookmarks / annotations.
- No multi-trace comparison / diff.
- No waveform / bus-load histograms.
- No PNG export (CSV only).
- No CAN-XL / J1939 multi-protocol decoding.
- No autonomous over-10k-signal scaling (palette caps at 10 charted signals simultaneously; user picks which 10 to plot).

## 3. Architecture

### Component diagram

```
            ┌────────────────────┐
            │ AppShellViewModel  │
            │  [ShowTraceViewer] │
            └─────────┬──────────┘
                      │ opens
                      ▼
            ┌────────────────────┐         ┌──────────────────────┐
            │ TraceViewerView    │  binds  │ TraceViewerViewModel │
            │ (non-modal Window) │◄────────┤ (UI-thread state)    │
            └─────────┬──────────┘         └──────────┬───────────┘
                      │ XAML binds                    │ owns
                      ▼                               ▼
            ┌────────────────────┐         ┌──────────────────────┐
            │ TraceChartViewModel│         │ ITraceViewerService  │
            │ + TraceChartSeries │         │  (Core/Replay)       │
            │   [N PlotModels]   │         └──────────┬───────────┘
            └────────────────────┘                    │ owns
                                                     ▼
                                          ┌──────────────────────┐
                                          │ ReplayTimeline       │
                                          │  (internal, reused)  │
                                          │  no sink injection   │
                                          └──────────────────────┘
```

### New files

```
src/PeakCan.Host.Core/Replay/
  ├── ITraceViewerService.cs             (new) — public interface
  └── TraceViewerService.cs              (new) — public sealed, no sink

src/PeakCan.Host.App/
  ├── ViewModels/
  │   ├── TraceViewerViewModel.cs        (new) — orchestrates load + playback + UI
  │   ├── TraceChartViewModel.cs         (new) — owns ObservableCollection<TraceChartSeries>
  │   └── TraceChartSeries.cs            (new) — per-signal data record
  └── Views/
      ├── TraceViewerView.xaml           (new) — non-modal Window
      └── TraceViewerView.xaml.cs        (new) — minimal code-behind

tests/PeakCan.Host.Core.Tests/Replay/
  └── TraceViewerServiceTests.cs         (new) — load, play, seek, no-sink assertions

tests/PeakCan.Host.App.Tests/
  ├── ViewModels/TraceViewerViewModelTests.cs    (new)
  ├── ViewModels/TraceChartViewModelTests.cs     (new)
  └── Composition/AppHostBuilderTests.cs         (updated) — register TraceViewer
```

### Reused (zero modification)

- `ReplayTimeline` (`internal sealed` in `PeakCan.Host.Core.Replay`) — constructed with three callbacks; the new service injects non-sink callbacks. **Note:** `internal` access is fine because `TraceViewerService` lives in the same assembly.
- `AscParser` — ASC parser.
- `ReplayFrame`, `ReplayState`, `PlaybackEndedEventArgs`, `ReplayExceptions` (`ReplayLoadException`, `ReplayFormatException`, `ReplaySendException` not used).
- `OxyPlot.Wpf` (2.2.0, already a project dependency) — `PlotView`, `PlotModel`, `LineSeries`, `LinearAxis`, `LineAnnotation`, `PlotController`, `PlotCommands`.
- Color palette (10 Tableau 10 colors) — copied as a `static readonly` field in `TraceChartViewModel`.
- `CommunityToolkit.Mvvm` — `[ObservableProperty]`, `[RelayCommand]`.
- `DbcService` (existing) — for DBC load + per-frame decoding.

## 4. Data flow

### 4.1. File open

```
User clicks "Open .asc"
  → TraceViewerViewModel.OpenFileCommand
  → FileOpenPicker (WPF dialog, ASC filter)
  → await TraceViewerService.LoadAsync(path, ct)
       → File.OpenRead(path)
       → AscParser.ParseAsync(stream, ct)        [reused]
       → ReplayTimeline.SetFrames(frames)
       → TotalDuration = last_frame.Timestamp
  → VM raises TraceLoaded event
  → VM calls DbcService.LoadAsync (if user previously chose a DBC) — see 4.2
  → VM calls BuildSignalList(frames, dbc)        // produces flat signal-list rows
  → VM calls BuildChartSeries(frames, dbc, signalList) // produces TraceChartSeries[]
  → TraceChartViewModel.Series = new ObservableCollection<TraceChartSeries>(...)
  → All PlotModels rendered to UI
```

### 4.2. DBC load

```
User clicks "Load DBC…"
  → FileOpenPicker (.dbc filter)
  → DbcService.LoadAsync(path)
  → VM raises DbcLoaded event
  → Re-runs BuildSignalList + BuildChartSeries with DBC available
  → Signal list updates (signal name, latest value, unit columns populate)
  → Chart subplots populate (raw → physical decode, per-signal Y axis range)
```

If user loads a DBC after the ASC trace is already loaded, the chart auto-rebuilds. If the user loads a DBC with no trace loaded yet, the DBC is held in the VM but no chart is built (charts need both).

### 4.3. Playback

```
User clicks ▶
  → TraceViewerService.Play()
  → ReplayTimeline.Play()         [internal, reused]
       → 1ms timer ticks
       → at each tick, computes frames where Timestamp <= now
       → for each frame, calls emit(frame) callback
            → TraceViewerService.EmitFrame (callback)
                 → does NOT call any sink
                 → raises FrameEmitted event on UI thread (via Dispatcher.InvokeAsync)
  → VM subscribes to FrameEmitted
       → updates PlaybackCursorX = frame.Timestamp
       → updates CurrentFrameIndex
       → re-evaluates LatestValue per signal at this frame (for left-list column)
       → updates PlaybackCursor LineAnnotation X on all subplots
            → throttled to ≤ 30 Hz; InvalidatePlot(false) so only cursor moves
```

### 4.4. Scrubber drag

```
User drags scrubber
  → TraceViewerViewModel.ScrubberValue (PropertyChanged, debounced 16ms)
  → ReplayTimeline.Seek(timestamp)  [internal, reused]
  → PlaybackCursorX = timestamp
  → Chart cursors re-positioned
  → Signal-list LatestValue column re-evaluated for the new cursor position
```

### 4.5. Measurement cursors

```
User clicks "⏱ Add cursor" (or clicks in chart area with Ctrl held)
  → TraceViewerViewModel.AddMeasurementCursor()
  → places yellow LineAnnotation at current PlaybackCursorX on all subplots
  → Status bar recomputes Δt (cursors[1] - cursors[0]) and Δvalue (per signal, value-at-time lookup)
  → second click replaces cursor[1]
  → "⏱⌫ Clear" removes both
```

## 5. Error handling

| Condition | Handling |
|---|---|
| File not found / IO error | `ReplayLoadException` (existing) → caught in VM → show MessageBox with "File: {path}\nReason: {message}" → window returns to empty state, no lockup |
| Malformed ASC | `ReplayFormatException` (existing) → same MessageBox path |
| Empty file (0 frames) | VM state: signal list empty, charts empty, scrubber disabled, status text "Empty file" |
| DBC parse failure | MessageBox; signal list shows raw CAN-IDs only (no Signal Name column populated); charts show no data ("Load DBC to plot signals") |
| DBC covers only some IDs | IDs not in DBC shown with "ID 0x{hex} (no DBC match)"; not chartable |
| User opens a second ASC while one is loaded | Pause current playback, unload current trace, load new one, reset cursor to t=0 |
| User closes window mid-playback | VM subscribes to `Window.Closing` → `TraceViewerService.Stop()` + `Dispose()`; timer thread exits cleanly |
| Replay of corrupt frames mid-playback | `OnTick` foreach catches exception per frame; `Re-entrantException` logged; playback continues to next frame (matches existing ReplayService tolerance) |
| File > 50,000 frames | Warn "Large file ({N} frames); UI may be slow. Continue?"; user can cancel |
| File > 100,000 frames | Hard cap; reject with MessageBox; suggest splitting or filtering |

## 6. UI layout (XAML structure)

```
Window
├── DockPanel
│   ├── Toolbar (DockPanel.Dock="Top")
│   │   ├── Open / Load DBC buttons
│   │   ├── Search TextBox
│   │   ├── Filter chips: CAN-ID + Start/End timestamps
│   │   ├── Playback controls: ▶ ⏸ ⏹ ⏮ + scrubber + Speed + Loop
│   │   └── Chart tools: Plot All / Plot None / Zoom-to-fit / Reset zoom / Add cursor / Clear cursors
│   │
│   ├── Status bar (DockPanel.Dock="Bottom")
│   │   └── Measurement readouts: "Cursor 1: t=X.XXX │ Cursor 2: t=Y.YYY │ Δt=Δ │ Values per signal"
│   │
│   └── Main grid (fills remaining)
│       ├── Column 0: Signal list DataGrid (Width="*", MinWidth="240")
│       │   ├── Plot checkbox column
│       │   ├── CAN ID
│       │   ├── Signal name
│       │   ├── Latest value
│       │   └── Unit
│       ├── GridSplitter (5px, vertical)
│       └── Column 1: Chart area
│           └── ScrollViewer
│               └── ItemsControl ItemsSource={Binding TraceChartViewModel.Series}
│                   └── ItemTemplate:
│                       └── Border (MinHeight=80, MaxHeight=250, default adaptive)
│                           ├── Header row: signal name + Color swatch + [Focus] [▼ Collapse] buttons
│                           └── oxy:PlotView with Model={Binding PlotModel}
```

**Adaptive subplot height algorithm:** when the chart area's actual height is `H` and `N` subplots are visible (excluding collapsed), each gets `H / N` clamped to `[80, 250]`. When focused, the focused subplot takes `H * 0.5`, others share `H * 0.5 / (N-1)`.

**Per-subplot `PlotModel` content:**
- `LinearAxis` `Position=Bottom`, `Minimum=0`, `Maximum=TotalDuration`, `MajorStep` auto
- `LinearAxis` `Position=Left`, range = `[min(value) - 5%, max(value) + 5%]`
- One `LineSeries` with all `(t, physicalValue)` points for this signal
- One `LineAnnotation` `Type=Vertical, Color=Red, StrokeThickness=2` (playback cursor)
- One or two `LineAnnotation` `Type=Vertical, Color=Yellow, StrokeThickness=1.5, LineStyle=Dash` (measurement cursors)
- `PlotController` configured for middle-mouse pan, Ctrl+wheel Y zoom, plain wheel X pan, Shift+left-drag rectangle zoom

**X-axis sync across subplots:** when any subplot's X-axis changes, all others' X-axes are updated to match. Implemented via `Axis.AxisChanged` event handler in `TraceChartViewModel.SyncXAxis(source)`.

## 7. Testing strategy

### Unit tests (Core)

- `TraceViewerServiceTests`
  - LoadAsync: file-not-found → `ReplayLoadException`
  - LoadAsync: malformed ASC → `ReplayFormatException`
  - LoadAsync: empty file → 0 frames, no throw
  - Play: cursor advances; PlaybackCursorX follows ReplayFrame.Timestamp
  - Seek: cursor jumps, no re-emission
  - Stop: cursor resets to 0, IsPlaying=false
  - **CRITICAL**: no `IReplayFrameSink` reference in ctor signature → assert via reflection that `TraceViewerService` does not take a sink param
  - **CRITICAL**: assert that during Play(), no `SendAsync` calls fire (sink-absence verified by mock framework substitute on `SendService`)

### Unit tests (App)

- `TraceChartViewModelTests`
  - AddSignal / RemoveSignal: plot model updates
  - LoadSignals: 1 series per `(id, signal)`, points are monotonic in X
  - GetStatistics: min/max/avg/n match expected
  - SyncXAxis: source change broadcasts to all series
  - PlaybackCursorX setter: propagates to all LineAnnotation.X

- `TraceViewerViewModelTests`
  - OpenFileCommand: integration with `TraceViewerService.LoadAsync`
  - DBC reload: signal-list columns populate
  - Filter by CAN-ID: matching rows visible
  - Search "0x102": matches ID column
  - Search "RPM": matches Signal column
  - Scrubber drag → ReplayTimeline.Seek
  - Window close → service.Stop + Dispose

### UI smoke tests

- Run the app, open an existing OEM `.asc` (e.g. from test fixtures), load the existing `Demo_Cdd.dbc`, confirm 5+ signals chart, scrubber drag updates playback cursor in real time.

### Coverage target

- ≥ 80% line / branch for new Core code
- ≥ 80% for new App code (with WPF UI exclusion on the auto-generated `InitializeComponent`)

## 8. Open risks

| Risk | Mitigation |
|---|---|
| 50k frames × decode × render on UI thread blocks startup | Decode + chart-build runs on `Task.Run`; UI shows progress bar |
| Subplot X-axis sync loop (cascading AxisChanged) | `IsSyncingXAxis` re-entrancy guard flag in `TraceChartViewModel` |
| Playback cursor at 30Hz + InvalidatePlot still CPU-heavy on 10 signals × 50k points | Throttle to 30Hz via `DispatcherTimer`; consider downsampling points when N > 5,000 (every-Nth strategy) |
| DBC file > 10 MB load time on UI thread | Use existing `DbcService.LoadAsync` (already async); if user complains, move to background |
| Multiple Trace Viewer windows open simultaneously | Out of scope: lazy-field pattern in AppShell allows only one, like Replay |

## 9. Spec self-review

- ✅ No TBD / TODO / placeholders.
- ✅ Architecture consistent with goals: every component exists for a reason.
- ✅ Scope single-implementation-plan sized: ~ 1 new service + 1 new VM + 1 chart VM + 1 view + ~ 6 new test files + 1 DI update = ≈ 1.5–2 weeks for one developer.
- ✅ No ambiguity: each section picks one approach.
- ✅ Error handling covers all expected failure modes.

## 10. Open questions (for user)

1. **DBC persistence across trace loads:** if user loads `trace.asc` + `engine.dbc`, then opens a different `trace2.asc`, should the DBC be re-applied automatically? **Recommend: yes, re-apply.** User explicitly loaded it; intent is "decode this trace with this DBC".
2. **Time-zero anchor for X axis:** Option A — first frame's timestamp = 0 (relative). Option B — wall-clock if ASC has absolute timestamps (parse `%date %time` headers). **Recommend: Option A** (always relative); matches what users expect from existing ReplayService and SignalView.
3. **Maximum simultaneously charted signals:** palette caps at 10. **Recommend: 10.** User can plot 11+ by manually collapsing some subplots, but `Plot All` stops at 10 with a tooltip.
4. **Where the Trace Viewer sits in the menu:** `View → Trace Viewer…` next to existing `View → Replay…`. **Recommend this.**
