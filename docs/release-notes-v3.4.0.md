# v3.4.0 MINOR — Trace Viewer Chart Series Production Wiring + Stroke Style

## What changed

**The Trace Viewer chart now renders.** In v3.2.0 / v3.3.x the
right-side chart panel was empty — only the X-axis cursor moved
across the panel. v3.4.0 wires up `TraceChartSeries` production
construction in `TraceViewerViewModel.RebuildSignalsAsync`: one
subplot per (source, signal) pair, populated with the source's
frames and decoded Y values. The X axis is shared across all
subplots (existing `SyncXAxis`). The Y axis is synchronized per
logical signal across all sources (v3.3.2 `SyncYAxes` is now
called from the new wiring, not just forward-looking).

Also adds **stroke style differentiation** for color-blind
accessibility: each loaded source gets a LineStyle from a 5-style
cycle (Solid / Dash / Dot / DashDot / DashDotDot) via
`ITracePalette.PickStrokeFor`. The stroke is applied to the
`OxyPlot.LineSeries.LineStyle` of each source's subplot. Sources
with visually similar colors (e.g., Tableau-10's blue + teal) are
now distinguishable by line pattern alone.

**Files modified (5):**
- `src/PeakCan.Host.App/Services/Trace/ITracePalette.cs` — added `LineStyle PickStrokeFor(string sourceId)` to the interface
- `src/PeakCan.Host.App/Services/Trace/TableauPalette.cs` — implemented `PickStrokeFor` (5-style cycle, 2 rounds for slots 0-9; hash fallback past 10)
- `src/PeakCan.Host.App/Services/Trace/TraceSource.cs` — added `LineStyle StrokeStyle = LineStyle.Solid` 5th field (init-only default for back-compat)
- `src/PeakCan.Host.App/Services/Trace/TraceSessionRegistry.cs` — `LoadAsync` calls `_palette.PickStrokeFor` and sets `meta.StrokeStyle` (mirrors the color-assignment try/catch so palette failure disposes the freshly-loaded service)
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` — `RebuildSignalsAsync` extended to populate `ChartViewModel.Series` (1 TraceChartSeries per source×signal) and call `SyncYAxes()` + `SyncXAxis(0, master.TotalDuration)` after population

**Test count:** 1058 + 6 SKIP / 0 fail (+8 net from v3.3.2's 1050; 2 new palette tests + 4 new chart-wiring tests + 2 more wiring tests).

**Closes 2 of 3 still-deferred v3.3.0 items:**
- ✅ Stroke style differentiation
- ✅ Trace Viewer chart series wiring (the foundational unblocker; not a v3.3.0 release-notes item but the prerequisite for closing several others)

**Still deferred (2 items):**
- `.tmtrace` bundle file format (save/restore multi-trace session) — v3.4.x
- Per-source `CanIdFilter` — v3.4.x

**Lessons:** 0 NEW. Foundational wiring is mechanically simple (the `RebuildSignalsAsync` extension follows the existing per-source iteration pattern).
