# Release Notes v3.14.3 — Trace Viewer DBC-driven signal table + opt-in chart rows (PATCH)

**Released:** 2026-07-08
**Parent:** v3.14.0 MINOR (`ada4162`)
**Tag:** v3.14.3
**Branch:** `feature/v3-12-0-minor`

## Highlights

This PATCH fixes the two Trace Viewer design anti-patterns the user reported after v3.14.2 PATCH landed:

1. **"add trace 后上来就把所有信号全信号展开了，非常吃电脑性能"** — v3.14.2 PATCH made per-signal PlotModel decode lazy, but the Trace Viewer still eagerly allocated 316 OxyPlot `PlotModel` placeholders the moment an .asc loaded. v3.14.3 PATCH removes the auto-build entirely: chart area is empty until the user explicitly opts in.
2. **"为什么一定要加载了asc之后，你才能在左边signal 表上显示内容，设计逻辑非常的怪"** — the signal table now shows ALL DBC signals the moment a DBC is loaded, independent of any ASC. Each row carries `Message / Signal / Unit / ☑ Plot / N / Latest` columns; `N` (frame count) and `Latest` populate as ASC arrives.

| Metric | Before (v3.14.2) | After (v3.14.3) |
|--------|---:|---:|
| Signal table visibility | Hidden until ASC loaded | **Always visible once DBC loaded**, ALL DBC signals with `N=0`, `Latest=NaN` |
| Chart subplots at ASC load | 316 placeholder OxyPlot `PlotModel`s | **0** — chart area empty |
| Chart subplot creation | (pre-v3.14.2: eager decode; v3.14.2: placeholder; both auto) | **User opt-in** via ☑ Plot checkbox |
| Opt-in cost | (n/a) | 2-50 ms per signal (v3.14.2 `PlotSignal` plumbing re-used) |
| Opt-ins survive ASC reload | (n/a) | **Yes** — opt-ins preserved on `SourcesChanged` |
| DBC reload (different DBC) | Full rebuild | Full rebuild + opt-ins reset (correct — different signals) |
| Test count | 1302 + 5 SKIP / 0 fail | **1312 + 3 SKIP / 0 fail** (+10 net) |

## What's in the box

| Commit | Fix | Behavior change |
|--------|-----|------|
| (local) | (1) `TraceSignalRow` extracted to its own file as `sealed partial class : ObservableObject` with INPC `IsPlotted`, `FrameCount`, `LatestValue` + new `MessageName` field. Stale xmldoc claim that `LatestValue` updates during playback corrected (it doesn't — it's set once at build time). | The signal table row is now a bindable class. Pre-v3.14.3 it was a `record` with a dead `IsPlotted` field. |
| (local) | (2) `BuildSignalRowsFromDbcOnly` replaces the old `BuildSignalRows` body — emits one row per (message, signal) regardless of frame presence. `FrameCount` + `LatestValue` populated from the bucket; `0` and `NaN` if no frames exist. | The signal table is now DBC-driven and independent of any ASC. |
| (local) | (3) `BuildChartSeries` is now a `[Obsolete]` no-op stub. Chart rows are NOT auto-built at load time. | The 316 OxyPlot placeholder rows no longer materialize when an .asc is loaded. |
| (local) | (4) New `BuildOneChartSeriesForSource` helper: shared body for `PlotSignal(TraceChartSeries)` (legacy path) and `PlotSignalFromTableRow` (new path). Returns one fully-populated `TraceChartSeries` per source. Honors per-source `CanIdFilter` override. | No code duplication between the two opt-in paths. Per-source filter respected. |
| (local) | (5) New `TogglePlot(TraceSignalRow)` RelayCommand + `SetPlotOptIn(TraceSignalRow, bool)` API. The XAML checkbox Click handler invokes `SetPlotOptIn(row, isChecked)`; tests and programmatic callers use the explicit API. | The user clicks ☑ Plot → one `TraceChartSeries` per source that has matching frames. Unchecking removes them. |
| (local) | (6) `OnRegistrySourcesChanged` no longer calls `ChartViewModel.Series.Clear()` + `RebuildSignalsCore()`. Replaced with `RefreshFrameCounts()` (in-place update of FrameCount + LatestValue per row) + `RemoveOrphanChartSeries()` (cleanup chart rows whose source got unloaded). | User opt-ins (chart rows) survive ASC add/remove. Only orphans get cleaned up. |
| (local) | (7) New `RefreshFrameCounts` + `RemoveOrphanChartSeries` private methods. | Frame counts and latest-value updates are now O(Signals) on `SourcesChanged` instead of O(Signals × frames). |
| (local) | (8) `OnAnySourcePropertyChanged` (per-source filter change) now calls `RefreshFrameCounts` + `RemoveOrphanChartSeries` instead of full `RebuildSignalsCore`. | User opt-ins survive per-source filter changes (consistent with SourcesChanged). |
| (local) | (9) `TraceViewerView.xaml`: DataGrid columns now `CAN ID / Message / Signal / Unit / Plot (☐) / N / Latest`. Plot column uses `DataGridTemplateColumn + CheckBox + Click handler` pattern (mirrors `SignalView.xaml:71-80` — `DataGridCheckBoxColumn` + `IsReadOnly=True` is broken on .NET 10). | The user has a UI affordance to opt in per signal. |
| (local) | (10) `TraceViewerView.xaml.cs`: new `OnPlotCheckboxClick` handler. Reads `cb.IsChecked` (UI value just toggled) → `vm.SetPlotOptIn(row, isChecked)`. Defensive `if (row.IsPlotted == isChecked) return;` skips stale reentry from virtualizing recycle. | UI clicks drive the opt-in path. |
| (local) | (11) Updated 13 existing tests + deleted 1 obsolete test (`BuildSignalRows_NoMatchingFrames_SkipsMessage`) + added 1 new test (`OnRegistrySourcesChanged_AfterOptIn_PreservesChartSeriesAndIsPlotted`). Test delta: +10 net. | Tests pin the new DBC-driven + opt-in contract. |

## Root cause

The Trace Viewer's left panel was an ASC-derived view of the DBC: it only showed signals that had matching frames in some loaded .asc. The right panel auto-built 316 OxyPlot subplot rows at ASC load even though the user had not opted into any of them. Both behaviors conflated two independent concepts:

- **Signal catalog** (DBC) — what signals *could* be observed. Independent of data sources.
- **Frame data** (ASC recordings) — what was actually observed. Per source.

v3.14.2 PATCH fixed the *decode* cost (lazy `PlotSignal`) but did not fix the *rendering cost* (316 placeholder OxyPlot `PlotModel`s) nor the *catalog-vs-data conflation* (signal table hidden until ASC loads). v3.14.3 PATCH fixes both.

## User-visible impact

| Scenario | Before (v3.14.2) | After (v3.14.3) |
|----------|-------------------|------------------|
| Open Trace Viewer, no DBC, no ASC | Empty left, empty right | Empty left, empty right (same) |
| Load DBC via DBC tab | Empty left, empty right | **Left populated with ALL DBC signals**, right empty |
| Load ASC via "Add trace…" | Left populated with N signals, **right populated with 316 chart placeholders** | **Left `N` column populates**, right still empty |
| User wants to see one signal's chart | (No UI affordance; would require a Plot button wired to v3.14.2's `PlotSignal`) | **Check ☑ Plot on the signal row** → one chart subplot appears (2-50 ms) |
| Add a 2nd ASC | (Opt-ins didn't exist) | **Existing opt-ins survive; new signal data appears in `N` column** |
| Unload a source | (Opt-ins were wiped on every ASC change) | **Only that source's chart rows removed**; other opt-ins preserved |
| Load different DBC | Full rebuild + opt-ins wiped | Full rebuild + opt-ins wiped (correct — different signals) |

## Lessons (1-of-1)

1. **`opt-in-ui-affords-better-perf-and-clarity-than-auto-build`** — design lesson. When a list contains N×M items (sources × signals), eager-build-at-load always costs O(N×M) and offers the user M items they don't want. Opt-in flips the cost to O(user-selected × M) and gives the user a clear, intentional mental model: "I see everything, I plot what I want." The Trace Viewer learned this the hard way: even with v3.14.2's lazy decode, allocating 316 OxyPlot `PlotModel`s at load time was still wasted work.

2. **`db-derived-catalog-should-not-depend-on-data-source`** — separation of concerns. A signal catalog (DBC) is independent of data sources (ASC recordings). Conflating them — "we only show signals that have frames" — makes the catalog invisible until data arrives. The catalog should be a DBC-derived view annotated with data stats (`N` frame count, `Latest` last decoded value). The Trace Viewer's left panel was an ASC-derived view of the DBC; v3.14.3 PATCH makes it a DBC-derived view with ASC stats layered on.

3. **`wpf-datagrid-checkbox-with-isreadonly-grid-needs-templatecolumn`** — WPF gotcha. `DataGridCheckBoxColumn` + `IsReadOnly=True` on the parent grid is broken on .NET 10 (clicks don't fire `CellEditEnding`). Use `DataGridTemplateColumn` + explicit `CheckBox` + `Click` handler. Same root cause as the v1.2.7 fix in `SignalView.xaml:57-70`. Documented and pinned.

4. **`binding-update-then-command-test-should-call-explicit-api`** — testing lesson. The `TogglePlot(row)` RelayCommand checks `row.IsPlotted` to decide plot vs unplot — but in production, the TwoWay binding updates `IsPlotted` BEFORE the Click handler fires. Tests calling `TogglePlot(row)` directly without first setting `IsPlotted=true` get unplot semantics. The fix: expose `SetPlotOptIn(row, bool)` as the testable API; `TogglePlot` stays for the XAML binding-aware path. Two methods, two purposes, both tested.

## NEXT

- v3.14.4 PATCH (or v3.15.0 MINOR): the 6 HIGH bugs from the review backlog (`G2 OdxImportService swallows OCE`, `H1 RecordService.StartRecording blocks UI thread`, `H6 RecordService dropped-frames counter broken`, `I1 UdsClient maxNumberOfBlockLength negative parse`, `I2 UdsClient.SecurityAccess SendKey SID overflow`, `I6 UdsSession.Dispose _s3Timer leak`).
- v3.15.0 MINOR: B1-B11 MEDIUM items from the original code review backlog. None are safety-critical; the Trace Viewer is now actually usable.
- Stream-parse + progress callback for very large .asc files (deferred since v3.9.0).