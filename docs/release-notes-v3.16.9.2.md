# Release Notes v3.16.9.2 — Trace Viewer X-axis Wall-Clock + Sample Markers (PATCH)

**Released:** 2026-07-10
**Parent:** v3.16.9.1 (`dace6d9` — UpdatePlaybackCursor throttle via Stopwatch)
**Tag:** v3.16.9.2
**Branch:** `feature/v3-12-0-minor`
**Spec:** `docs/superpowers/specs/2026-07-09-trace-viewer-enhancements-design.md`
**Plan:** `docs/superpowers/plans/2026-07-10-trace-viewer-enhancements-remaining.md`

## Highlights

Closes the **last 2 unfulfilled items** from the 2026-07-09
trace-viewer-enhancements spec, after 3 of 6 spec items were already
implemented by the WallClockOrigin chain (commits `f9886e3`..`ea51d2f`).

### What's now visible on screen

1. **X-axis shows wall-clock time when the ASC file carries a `date`
   header.** For a recording dated `Wed Jul 1 08:32:01 am 2026`, the
   X axis now displays `07/01 08:32:01`, `07/02 08:32:01`, etc. when
   the source carries a `WallClockOrigin` (parsed from the ASC header
   by the WallClockOrigin chain).

   When the source has no `WallClockOrigin` (no `date` header in ASC,
   ~5% of traces), the X axis falls back to a 3-tier elapsed formatter:
   - `x >= 1 day`: `1.0d 01:01:01`
   - `x >= 1 hour`: `01:02:05`
   - `x < 1 hour`: `02:05.5`

   Uses `CultureInfo.InvariantCulture` so locale cannot change the
   `MM/dd` ordering or the decimal point.

2. **Each CAN frame shows as a circle marker on the chart line.**
   `MarkerType = MarkerType.Circle`, `MarkerSize = 3`. Now the user
   can distinguish:
   - **line** = trend / interpolation between samples
   - **circle** = real CAN frame at this timestamp

   Without markers, OxyPlot's default `MarkerType = None` renders a
   continuous line with no per-point visibility.

## What this PATCH explicitly does NOT do

(Spec items already addressed by prior commits — kept here so the
record is complete.)

- **Play/Pause/Stop button visibility** — spec §3.5 line 3-5 claim
  "Play is DEAD as of v3.18.0" is **wrong**; v3.18.0 does not exist
  (latest release is v3.16.8.2). Buttons remain visible in
  `TraceViewerView.xaml` line 94-99, controlled by `HasSources` boolean.
  No change.

- **`AscParser.ParseAsync` extension** — spec §3.1 line 61-94 plan to
  "extend `ParseAsync` to return `DateTime?`" was implemented
  differently: HEAD commit `f9886e3`/`ce56f27` added a new overload
  `ParseAsyncWithHeaderAsync(...)` returning `AscParseResult`
  (frames + origin + AbsoluteTimestamps mode). Old `ParseAsync`
  remains unchanged. The end-to-end behavior matches the spec.

- **Shared `LineAnnotation` across all subplots** — spec §3.5 line
  200-204 design "one LineAnnotation owned by TraceChartViewModel,
  referenced by every PlotModel" was speculative. v3.16.9 (commit
  `1dbc0b1`) created per-subplot LineAnnotation, and v3.16.9.1 (commit
  `dace6d9`) throttled `UpdatePlaybackCursor`. The per-subplot
  approach is functionally equivalent (each subplot's annotation
  receives the same X on every frame, so the cursor visually aligns).

## Files in this PATCH

```
src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs    (+25 LoC)
  - using System.Globalization; (NEW import, line 3)
  - BuildOneChartSeriesForSource: extract bottomAxis local + attach
    LabelFormatter lambda (4 branches: wall-clock + 3 elapsed tiers)
    + LineSeries adds MarkerType=Circle, MarkerSize=3

tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs    (+83 LoC)
  - using OxyPlot.Axes; + using OxyPlot.Series; (NEW imports)
  - BuildOneChartSeriesForSource_LineSeries_HasMarkerTypeCircle
  - BuildOneChartSeriesForSource_XAxis_WithWallClockOrigin_FormatsAsMmDdHhMmSs
  - BuildOneChartSeriesForSource_XAxis_WithoutWallClockOrigin_FallsBackToElapsed
    (Theory: 3 InlineData cases covering >=1d / >=1h / <1h)

docs/superpowers/plans/2026-07-10-trace-viewer-enhancements-remaining.md (NEW)
docs/release-notes-v3.16.9.2.md (this file)
```

## Tests

- **New tests**: 5 (1 marker + 1 wall-clock formatter + 3 elapsed fallback InlineData)
- **New tests pass**: 5/5
- **Pre-existing failures**: 3 `RebuildSignalsAsync_*` tests fail with
  empty `sut.Signals` on DBC-loaded + frames-present path. **Verified
  via `git stash` that these fail on HEAD `52cf3d6` without this
  PATCH's changes** — caused by commit `ea51d2f` ("switch to
  header-aware parser + expose LastParseResult") changing
  `TraceViewerService` init flow. **Out of scope** for this PATCH;
  to be addressed in a dedicated v3.16.9.3 follow-up PATCH.
- **Full test result**: Core.Tests 448 pass / 0 fail;
  Infrastructure.Tests 85 pass / 0 fail / 2 skip;
  App.Tests 793 pass / 3 fail (pre-existing) / 3 skip.

## Why this PATCH ships only 2 of 6 spec items

The 2026-07-09 spec listed 6 enhancements across 3 user concerns.
By 2026-07-10, the WallClockOrigin chain had already implemented 3 of
them via a different design (overload instead of extension; per-source
field instead of service-level). Of the remaining 3, one
(spec §3.5 "shared LineAnnotation") was a speculative design that
turned out to be unnecessary given the per-subplot approach already
in place. The remaining 2 (X-axis formatter + MarkerType) are
high-value and shipped here.

## Lessons (1 NEW, 1 CONFIRMED)

1. **`spec-hypothetical-design-vs-code-reality-must-be-validated-before-execution`** —
   NEW lesson. The 2026-07-09 spec assumed (a) Play was DEAD in
   v3.18.0 (false — v3.18.0 doesn't exist), (b) `AscParser.ParseAsync`
   needed extension (false — already implemented as new overload),
   (c) shared `LineAnnotation` was needed (false — per-subplot works).
   All 3 hypothetical assumptions were detectably wrong by reading
   current HEAD before starting. The plan §0 documents the
   reality check, but the upstream spec did not. **Process fix**:
   spec authors should always include a "Reality check vs HEAD"
   section in specs that target existing code; spec reviewers should
   reject specs that lack it.

2. **`git-stash-as-regression-isolator`** — confirmed. When a test
   fails after a code change, `git stash` reverts to HEAD and re-runs
   the failing test. If it still fails, the failure is pre-existing
   (not caused by the change). This 30-second check is much cheaper
   than a full bisect. Documented in plan §6.1.

## Risk notes

- **R1**: `MarkerType.Circle` + `MarkerSize=3` may visually merge on
  dense traces (10 kHz+ CAN, traces > 60 seconds). Mitigation:
  follow-up PATCH may lower to `MarkerSize=2` or make size adaptive.
  Not blocking.
- **R2**: `DateTimeKind.Local` interpretation may shift across DST
  transitions. Mitigation: traces that span DST boundaries are rare
  in CANoe recordings; if encountered, fix is to use
  `DateTimeKind.Utc` and convert ASC `date` to UTC. Not blocking.
- **R3**: `F1` format produces "1.0d" — spec explicitly chose this for
  visual consistency (always 1 decimal place). If user prefers "1d",
  easy follow-up to switch to `F0` for whole-day traces.