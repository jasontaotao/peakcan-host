# v3.3.2 PATCH — Cross-Source Y-Axis Auto-Scale Coordination

## What changed

Adds `TraceChartViewModel.SyncYAxes()` — a stand-alone, testable
method that groups chart subplots by logical `SignalKey` and sets
each group's Y axis (Left `LinearAxis`) to the union min/max of all
sources' Y data, with 5% padding for visual breathing room.

**Forward-looking scope.** `TraceChartSeries` construction is still
unwired in v3.3.x production code (no caller of
`TraceChartViewModel.AddSeries` outside tests), so `SyncYAxes` is
currently unreachable at runtime. The method is tested in isolation
against the existing `TraceChartViewModelTests.MakeSeries` helper.
When v3.4.0 wires up chart series construction, `RebuildSignalsAsync`
will call `SyncYAxes()` after populating the series — and the Y-sync
logic is already in place.

**Files modified (2):**
- `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` — added `public void SyncYAxes()` after `SyncXAxis`. Groups by `SignalKey` (NOT `EffectiveKey` — we want same-signal-across-sources sharing).
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs` — added 3 tests (single source, 2-sources-same-signal, 2-sources-different-signals).

**Test count:** 1050 + 6 SKIP / 0 fail (+3 net from v3.3.1's 1047).

**Closes 1 of 4 still-deferred v3.3.0 items:**
- ✅ Cross-source Y-axis auto-scale coordination

**Still deferred (3 items):**
- `.tmtrace` bundle file format
- Per-source `CanIdFilter`
- Stroke style differentiation (deferred to v3.4.0 — blocked on `TraceChartSeries` production wiring)

**Lessons:** 0 NEW. Surgical additive change.
