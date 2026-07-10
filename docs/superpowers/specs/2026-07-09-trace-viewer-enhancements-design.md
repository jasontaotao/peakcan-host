# Trace Viewer Enhancements — Design

> **Play is DEAD as of v3.18.0.** The Play / Pause / Stop buttons are
> hidden (`Visibility="Collapsed"` in `TraceViewerView.xaml` line 101-106)
> because the cursor / playback chain was unfixable across 14+ PATCH
> attempts. This spec does NOT add a Play path. Cursor movement in this
> spec is **slider-driven only** (the existing scrubber `Slider` on
> `TraceViewerView.xaml` line 107-110). Users drag the slider to seek
> the master timeline; the new shared time cursor follows.

## 1. Overview

Three user-reported Trace Viewer enhancements, each addressing a separate
limitation of the current X-axis / chart surface:

1. **X-axis time display** — current `double` seconds (e.g. `155564.4328`)
   is hard to read at a glance; user wants the X axis to show real wall-clock
   time from the ASC recording when available.
2. **Cross-signal same-moment read** — current subplots are independently
   scrolled/zoomed; user wants a single vertical "time cursor" shared
   across all subplots so they can read the value of every signal at one
   timestamp. **Driven by the slider only** (no Play, no auto-advance).
3. **Real sample-point visibility** — current `LineSeries` connects all
   samples into a continuous line; user wants the discrete CAN sample
   points to be visually distinct from the connecting line.

Combined scope: ~5 files, ~150 LoC net, no data-model change to
`ReplayFrame` / `ITraceViewerService.FrameEmitted` shape. **No Play
feature is touched, restored, or simulated in any of the changes below.**

## 2. Goals

- **G1**: When an ASC file carries a `date Wed Jul 1 08:32:01.000 am 2026`
  header, the X axis must show wall-clock time derived from
  `WallClockOrigin + frame.Timestamp`, formatted as `MM/DD HH:mm:ss` (or
  `dd:HH:mm:ss` for short traces).
- **G2**: A single user-controlled time cursor (vertical line) must be
  visible across every subplot at the same X. **The cursor is driven
  by the existing slider only** (Play is dead — see file header; this
  spec does not add any auto-advance or frame-driven cursor motion).
- **G3**: Every plot must visually mark each discrete sample as a circle
  marker on top of the existing line, so the user can see "this is where
  the CAN frame actually fired" vs "this is the interpolating line
  between samples".
- **G4 (non-goal)**: Do NOT change `ReplayFrame.Timestamp` semantics.
  The absolute-seconds numeric remains a `double`; wall-clock formatting
  is a presentation-layer concern only.
- **G5 (non-goal)**: Do NOT add a "X-axis format" toggle. If the source
  has no `date` header (≈5% of traces), the X axis falls back to the
  current `mm:ss` / `hh:mm:ss` auto-scale. The user can read either form.
- **G6 (non-goal)**: Do NOT add a "click on chart to set cursor" gesture.
  YAGNI for this PATCH. Slider is the only cursor control. Future work.
- **G7 (non-goal)**: Do NOT add a hover tracker / crosshair. Slider
  drag is the only way to move the cursor. OxyPlot's built-in tracker
  remains disabled.

## 3. Architecture

### 3.1 AscParser — parse the `date` header line

**Decision**: Extend `AscParser.ParseAsync` to extract the wall-clock
origin from the `date ...` header line, and the timestamp mode from
`base hex  timestamps absolute` / `base hex  timestamps relative` (Vector
CANoe's exact header form).

**New output**: alongside `_frames`, the parser also returns
`DateTime? wallClockOrigin`. The caller (`TraceViewerService.LoadAsync`)
binds this to the new `TraceSource.WallClockOrigin` field.

**Rationale**:
- The user has confirmed the ASC header **is** authoritative: `date`
  sets the absolute starting wall-clock; `timestamps absolute` confirms
  the numeric column is seconds-since-epoch (155564 ≈ 43.2 h matches
  the file dated Wed Jul 1 08:32:01 — frames were captured ~43 h later).
- The parser is the only layer that sees the raw header lines; lifting
  the value to a typed field at the parse site keeps the rest of the
  stack decoupled from the ASC text grammar.
- If the `date` line is absent (some Vector exports skip it) or
  unparseable, the parser returns `null` and the X-axis formatter falls
  back to elapsed time.

**Alternatives considered**:
- **Parse the `date` line in `TraceViewerService.LoadAsync`** after
  `ParseAsync` returns: would require a second stream read, or buffering
  the header lines through a `Lazy<>` indirection. Lifting the parse
  into `AscParser` is one pass, no double-read.
- **Treat all timestamps as relative** (current behavior) and add a
  user-supplied "date header" input: error-prone (user can mistype the
  date), and the data is already in the file.
- **No-op** (do not implement wall-clock): rejected — the user
  explicitly asked for it.

### 3.2 TraceSource — add `WallClockOrigin`

**Decision**: Add `public DateTime? WallClockOrigin { get; set; }` to
`TraceSource`. Null means "no header, fall back to elapsed display".

**Rationale**:
- `TraceSource` is the per-source identity in the registry; the wall-clock
  origin is a property of the source, not of the registry or library.
  Tying it to the source keeps the field valid for the source's lifetime
  and lets the chart VM format each subplot's X axis from its own source.
- Multi-trace overlays already fork each source independently, so each
  subplot can have its own origin without coordination.

**Alternatives considered**:
- **Global `IClockOrigin` service**: over-engineered for one feature.
- **Library-level list of origins**: awkward — `TraceSessionLibrary` is
  the persistence layer, not a transient state holder.

### 3.3 TraceViewerService — bind origin to source

**Decision**: After `AscParser.ParseAsync` returns
`(frames, wallClockOrigin)`, the service sets
`_registry.GetSource(srcId).WallClockOrigin = wallClockOrigin`.

**Rationale**:
- The service is the boundary between parsing and VM. Binding here keeps
  the parser's output typed and the VM's `TraceSource` accessor typed —
  no string parsing leaks into the VM layer.
- The registry's source list is already populated at this point (the
  service has just `LoadAsync`-ed one source), so the binding is a
  straight property set.

### 3.4 X-axis formatter — wall-clock or elapsed

**Decision**: When `TraceChartViewModel.BuildOneChartSeriesForSource`
(or equivalent in `TraceViewerViewModel`) creates the
`PlotModel.Axes[Bottom]`, attach an `AxisLabelFormatter` lambda:

```
double -> string
if (source.WallClockOrigin is { } origin)
    return (origin + TimeSpan.FromSeconds(x))
        .ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
else if (x >= 86400) return $"{x/86400:F1}d {TimeSpan.FromSeconds(x):hh\\:mm\\:ss}";
else if (x >= 3600)  return TimeSpan.FromSeconds(x).ToString(@"hh\:mm\:ss");
else                  return TimeSpan.FromSeconds(x).ToString(@"mm\:ss\.f");
```

**Rationale**:
- Single property (`AxisLabelFormatter`) on `LinearAxis`. OxyPlot WPF
  binding refreshes the labels on every `InvalidatePlot`; no extra
  wiring needed.
- `CultureInfo.InvariantCulture` prevents the user's locale from changing
  the `MM/dd` ordering and the decimal point.
- The 3-tier elapsed fallback (≥1d, ≥1h, <1h) is the same pattern
  `TimeSpan.ToString` uses; it produces readable labels at every
  timescale without an "X-axis format" toggle (G5).

**Alternatives considered**:
- **Replace `double` with `DateTime` on the X axis**: would require
  every consumer (slider, scrubber, seek math) to convert back to
  seconds; massive blast radius. Rejected.
- **Custom `LabelFormatter` class instead of a lambda**: OxyPlot WPF
  accepts either; the lambda is shorter and more local.

### 3.5 Shared time cursor — single vertical line, all subplots

**Decision**: Move the playback-cursor `LineAnnotation` from
`TraceChartViewModel.AddSeries` (per-PlotModel) to a **single** shared
instance owned by `TraceChartViewModel`. Update it once in
`SetTimeCursor(double x)`; each subplot's `PlotModel` references the
same `LineAnnotation` object.

> **Cursor movement is slider-driven only.** Play is dead (see file
> header). The only trigger for `SetTimeCursor` is the existing
> `ScrubberValue` change handler:
>
> ```
> partial void OnScrubberValueChanged(double value) {
>     if (TotalDuration > 0 && _masterService is not null) {
>         SeekAllToProportionalTime(value);
>         ChartViewModel.SetTimeCursor(value);   // NEW
>     }
> }
> ```
>
> When the user drags the slider, `ScrubberValue` updates → the
> partial method fires → `SeekAllToProportionalTime` re-anchors every
> service timeline (existing) AND the new `SetTimeCursor` call moves
> the shared `LineAnnotation` to the same X. Every subplot's
> `PlotModel` re-renders with the cursor at the new X.
>
> **No `OnAnyFrameEmitted` is involved.** The frame-emit path is
> abandoned (Play is dead). The existing
> `ChartViewModel.UpdatePlaybackCursor(double x)` method is left in
> place for any future re-attempt, but **nothing in this spec calls
> it** during normal operation — the cursor is purely a function of
> `ScrubberValue`.

**Optional follow-up (NOT in this spec)**: a click-on-chart handler
that converts the click's pixel X to data X and calls
`SetTimeCursor(dataX)` plus `vm.ScrubberValue = dataX`. This would let
the user click anywhere in a subplot to jump the cursor, instead of
having to drag the slider. **Out of scope for this PATCH** — YAGNI;
can be added later if users complain.

**Rationale**:
- The v3.18.0 PATCH (since reverted in this session) created one
  `LineAnnotation` per subplot, all at the same X. That works for a
  single visual line, but does not address G2 — the user wants one
  cursor that moves with the slider, visible on every subplot at the
  same X.
- A shared `LineAnnotation` referenced by every subplot's
  `PlotModel.Annotations` is the natural WPF / OxyPlot pattern: a
  single `INotifyPropertyChanged` source feeds multiple consumers.
- Tying the cursor to the slider means the user's existing mental
  model (drag slider → seek) is preserved; they get the new
  "shared-across-subplots" behavior for free without learning a new
  gesture.

**Alternatives considered**:
- **One LineAnnotation per subplot, all updated together**: same number
  of `InvalidatePlot` calls (one per subplot), but the "shared" feel
  is lost — if the user later pans one subplot, the other cursors
  drift out of sync. Shared reference avoids that.
- **Custom `Tracker` (OxyPlot's hover crosshair)**: tracks the mouse,
  not the slider. Rejected — the user wants a persistent cursor
  tied to a real timeline value, not a transient hover indicator.
- **Restore Play to drive the cursor**: rejected — Play is dead, do
  not resurrect it as a side effect of this spec.

### 3.6 LineSeries marker — show real sample points

**Decision**: In `BuildOneChartSeriesForSource`, set
`new LineSeries { MarkerType = MarkerType.Circle, MarkerSize = 3, ... }`
on the existing line. OxyPlot renders a circle at every (x, y) point
on top of the connecting line.

**Rationale**:
- One property change. `MarkerType.Circle` is the default-style marker
  (filled disc). `MarkerSize = 3` is small enough to not occlude the
  line but large enough to be visible at 1920×1080.
- The user can visually distinguish: **line** = "this is the trend";
  **circle** = "this is a real CAN frame at this timestamp".
- No new OxyPlot type needed; the existing `LineSeries` already
  supports markers (it's how OxyPlot distinguishes "trend" from
  "scatter" — both render in the same series).

**Alternatives considered**:
- **`ScatterSeries` only (no line)**: would discard the trend, which
  the user wants to keep ("RPM 加速趋势" was the user's example).
- **`StemSeries` (lollipop)**: requires Y=0 as the line base; if a
  signal's range is e.g. [-50, 200] the lollipops all crowd the top.
  Rejected.
- **Toggle line / line+marker / sticks** (3 modes): YAGNI. G3 only asks
  for "突出" the points, not for a 3-way choice.

## 4. Data Flow

```
ASC file
  ↓ AscParser.ParseAsync (parses "date" header + frames)
(DateTime? wallClockOrigin, IReadOnlyList<ReplayFrame>)
  ↓ TraceViewerService.LoadAsync binds
TraceSource.WallClockOrigin = wallClockOrigin
  ↓ TraceViewerViewModel.AddToWatch → BuildOneChartSeriesForSource
PlotModel.Axes[Bottom].LabelFormatter = wall-clock-or-elapsed lambda
PlotModel.Series[0] = LineSeries { MarkerType = Circle, MarkerSize = 3 }
PlotModel.Annotations = [sharedTimeCursor]   (single instance, all subplots)

User drags Slider
  ↓ Slider Value = ScrubberValue (TwoWay binding, existing)
  ↓ OnScrubberValueChanged partial method (existing)
  ├─ SeekAllToProportionalTime(value)         (existing — seeks each service)
  └─ ChartViewModel.SetTimeCursor(value)      (NEW — moves shared cursor)
        → sharedTimeCursor.X = value
        → foreach PlotModel: InvalidatePlot(false)
        → N subplots re-render with cursor at value
```

## 5. Error Handling

- **No `date` header in ASC**: `AscParser` returns `null` origin;
  `TraceSource.WallClockOrigin` stays null; X axis uses the 3-tier
  elapsed fallback. No error message — silent fallback.
- **Unparseable `date` line** (e.g. locale-specific text the parser
  does not recognize): parser returns `null`, same fallback.
- **`base hex timestamps relative`**: parser does NOT add the wall-clock
  origin (the timestamps are already relative). The X axis stays
  elapsed-only. Documented in release notes as a known Vector CANoe
  variant.
- **Multi-trace overlay with different origins**: each subplot's X axis
  uses its own source's origin. If two sources have different origins,
  the two subplots' X-axis labels differ — same X value displays two
  different wall-clock times. This is a corner case (recordings from
  two different days) and is acceptable; we do not unify.

## 6. Testing

### 6.1 AscParser unit tests (RED → GREEN)

- `ParseAsync_AsciiWithDateHeader_ReturnsWallClockOrigin`
  - Input: the user's sample `date Wed Jul 1 08:32:01.000 am 2026\nbase hex  timestamps absolute\n…`
  - Assert: `result.WallClockOrigin == new DateTime(2026, 7, 1, 8, 32, 1, DateTimeKind.Local)`
- `ParseAsync_AsciiWithoutDateHeader_ReturnsNullOrigin`
  - Input: header-less trace
  - Assert: `result.WallClockOrigin is null`
- `ParseAsync_AsciiWithRelativeTimestamps_ReturnsNullOrigin`
  - Input: `base hex  timestamps relative`
  - Assert: `result.WallClockOrigin is null`

### 6.2 TraceSource test (RED → GREEN)

- `TraceSource_WallClockOrigin_DefaultsToNull`
  - Assert: `new TraceSource(...).WallClockOrigin is null`
- `TraceSource_WallClockOrigin_RoundTripsThroughBundle`
  - Assert: serialize/deserialize preserves the value

### 6.3 TraceChartViewModel formatter test (RED → GREEN)

- `AxisLabelFormatter_WithWallClockOrigin_FormatsAsMmDdHhMmSs`
- `AxisLabelFormatter_WithoutWallClockOrigin_FallsBackToElapsed`
  - Three sub-cases: <1h, <1d, ≥1d

### 6.4 Shared cursor test (RED → GREEN)

> **No Play / OnAnyFrameEmitted tests.** Play is dead. The cursor
> is driven by the slider only. The shared-cursor tests cover the
> **state model** (single instance, all subplots see the same X) and
> the **slider integration** (`OnScrubberValueChanged` calls
> `SetTimeCursor`). They do NOT test any auto-advance / frame-emit
> path.

- `AddSeries_AllSeries_ReferenceSameTimeCursorInstance`
  - Assert: after `AddSeries` for 3 series, all 3 `PlotModel.Annotations`
    contain the **same** `LineAnnotation` reference.
- `SetTimeCursor_UpdatesAllSeries_AtOnce`
  - Assert: after `SetTimeCursor(123.0)`, the shared annotation's `X` is
    123.0; the formatter renders consistently across all series.
- `OnScrubberValueChanged_CallsSetTimeCursor`
  - Assert: setting `sut.ScrubberValue = 42.0` results in
    `sut.ChartViewModel` seeing the shared cursor's `X = 42.0` after
    the partial method runs.
- `OnScrubberValueChanged_DoesNotCallUpdatePlaybackCursor`
  - Assert: `OnAnyFrameEmitted` is **not** called by the slider path.
    (Guards against a future regression that wires Play back in by
    accident.)

### 6.5 Marker test (RED → GREEN)

- `BuildOneChartSeriesForSource_LineSeries_HasMarkerTypeCircle`
  - Assert: `series.PlotModel.Series[0].MarkerType == MarkerType.Circle`
  - Assert: `series.PlotModel.Series[0].MarkerSize == 3`

### 6.6 End-to-end smoke (manual, in a follow-up PATCH)

> **Smoke does NOT use Play.** Play buttons are hidden. Cursor
> movement is verified by dragging the slider.

- Open the user's sample ASC, confirm the X axis shows
  `07/01 08:32:01` at t=0, `07/03 03:44:45` at t=155564.433, etc.
- Drag the slider to t=10000. Confirm: every subplot's vertical
  cursor line moves to X=10000; the X-axis label at that point is
  `07/01 11:18:21` (or whatever the wall-clock value is).
- Visually confirm a circle marker appears on every line point.
- Confirm Play / Pause / Stop buttons are still hidden
  (no regression on the v3.18.0 hide).

## 7. Out of Scope

- **Wall-clock X axis for the playback-cursor `LineAnnotation` itself**:
  the cursor is a vertical line at a single X; the X label is on the
  bottom axis, not on the cursor. The cursor does not need its own
  timestamp tooltip in this PATCH.
- **Hovering the line to show `(t, value)`**: OxyPlot's built-in
  `Tracker` provides this; the user did not ask for it. YAGNI.
- **Auto-detect whether timestamp is "seconds-since-epoch" or
  "seconds-since-start-of-file"**: the user's file says
  `timestamps absolute`, so we trust it. If a future file lacks the
  `base hex  timestamps …` line, the parser assumes relative and the
  wall-clock origin is unused.
- **A date-picker / timezone-selector UI**: the `date` line's time is
  local (no timezone suffix in Vector's format). The user can correct
  this by editing the ASC; we do not add a UI for it.

## 8. Risks

- **R1**: The `MarkerType = Circle` may overlap on dense traces
  (10 kHz+ CAN), making the chart look like a solid line. Mitigation:
  `MarkerSize = 3` is small; if a future test surfaces this as a real
  issue, lower to `2` or make size adaptive to point density. Not
  blocking.
- **R2**: `AxisLabelFormatter` lambda captures `source.WallClockOrigin`;
  if the origin is mutated after the axis is created, the formatter
  closes over the old value. Mitigation: capture by reference (the
  `TraceSource` instance), not by value, and re-resolve at format time.
  Done by accessing `source.WallClockOrigin` inside the lambda body.
- **R3**: Shared `LineAnnotation` across multiple `PlotModel`s is a
  well-known OxyPlot pattern but the `PlotModel.Annotations` collection
  may dedupe by reference (it should not, but worth verifying in test
  6.4). If it dedupes, the test catches it.
- **R4**: Wall-clock labels in `MM/dd HH:mm:ss` are 14 characters
  wide; a tight subplot may clip. Mitigation: short format
  `MM/dd\nHH:mm:ss` (OxyPlot supports multi-line labels via `\n`).
  Not blocking; can be tuned in a follow-up.

## 9. Affected Files

> **No Play-related files are touched.** `PlayCommand` / `PauseCommand` /
> `StopCommand` / `OnAnyFrameEmitted` / `ReplayTimeline.Play` /
> `TraceViewerService.Play` all remain as-is (with the buttons hidden
> via XAML `Visibility="Collapsed"` since v3.18.0). This spec does
> **not** modify, restore, or simulate any of them.

1. `src/PeakCan.Host.Core/Ascii/AscParser.cs` — parse `date` + `base hex`
2. `src/PeakCan.Host.App/Services/Trace/TraceSource.cs` — add
   `WallClockOrigin` field
3. `src/PeakCan.Host.Core/Replay/TraceViewerService.cs` — bind origin
   from parser output to `TraceSource`
4. `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` — X-axis
   formatter, shared cursor, marker
5. `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` — wire
   formatter into `BuildOneChartSeriesForSource` AND add
   `SetTimeCursor(value)` call inside the existing
   `OnScrubberValueChanged` partial method
6. `src/PeakCan.Host.Core/Ascii/AscParserTests.cs` — new tests
7. `tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs`
   — new tests
8. `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`
   — new tests

**NOT touched (must not change in this PATCH)**:
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml` — Play/Pause/Stop
  buttons stay `Visibility="Collapsed"`
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` lines
  for `Play` / `Pause` / `Stop` / `OnAnyFrameEmitted` — leave as-is
- `src/PeakCan.Host.Core/Replay/TraceViewerService.cs` `Play` /
  `Pause` / `Resume` / `Stop` methods — leave as-is
- `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` — leave as-is

## 10. Open Questions

- (none at design time; resolved during brainstorm: per-source origin
  field, no toggle, line+marker only, all subplots share one cursor
  instance)
