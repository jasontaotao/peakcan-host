# v3.2.0 MINOR — Multi-Trace Overlay (Trace Viewer extension) (2026-07-04)

## Summary

Lifts the v3.0 design doc's explicit non-goal at line 40
("No multi-trace comparison / diff.") and ships multi-trace overlay
support for the Trace Viewer. Users can now load 2+ recorded traces
into the same session and see them color-coded on the same chart —
the most-requested Trace Viewer feature for regression-test workflows
("did signal X behave the same way before and after a code change?").

The single-trace workflow (1 source) is a degenerate case of the new
session registry — **v3.0/3.1.x behavior is preserved end-to-end** when
only one ASC is loaded. Multi-trace mode (≥2 sources) is **read-only**
(static comparison); sync playback across N sources is deferred to
v3.3.0 (proportional seek math + master-source dropdown UI is
non-trivial).

**10 files modified, 8 new files (+~750 / −~80 net).**

## Files modified

- `src/PeakCan.Host.App/ViewModels/TraceChartSeries.cs` — adds `SourceId`
  (init-only, default `""`) + computed `EffectiveKey` for chart-internal
  lookups (`{SourceId}.{SignalKey}` when non-empty, falls back to
  `SignalKey` for v3.0 callers).
- `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` — internalizes
  `EffectiveKey` in `RemoveSeries` / `ToggleCollapse` / `SetFocus` so the
  same logical signal from two traces is disambiguated.
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` — major rewrite.
  Replaces single `_service : ITraceViewerService` with
  `_registry : ITraceSessionRegistry`. Adds `AddTraceCommand` +
  `RemoveTraceCommand`. Multi-trace mode (Sources.Count > 1) DISABLES
  `Play` / `Pause` / `Stop` / `SeekTo` (throw `InvalidOperationException`
  with message "Playback disabled in multi-trace mode (v3.3.0 will add
  sync playback across N sources)"). Computes `IsMultiTraceMode`,
  `PlaybackControlsVisibility`, `HasSources` for XAML bindings.
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml` — toolbar button
  "Open .asc…" renamed "Add trace…" (always appends to session, never
  overwrites). Play/pause/stop/scrubber hidden via
  `PlaybackControlsVisibility`. New per-source legend strip with
  colored swatch + ✕ button (via `RemoveTraceCommand`).
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs` — replaces
  `OnOpenAscClick` with `OnAddTraceClick` (calls `vm.AddTraceAsync`).
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` — replaces
  `AddSingleton<ITraceViewerService, TraceViewerService>()` with
  `AddSingleton<ITracePalette, TableauPalette>()` +
  `AddSingleton<ITraceSessionRegistry, TraceSessionRegistry>()`.
- `src/PeakCan.Host.App/App.xaml` — registers two new global resources:
  `BooleanToVisibilityConverter` and `OxyColorToBrushConverter`.
- Tests: `TraceViewerViewModelTests` (migrated 11 sites to
  `ITraceSessionRegistry`), `AppShellViewModelTests` (5 sites),
  `AppHostBuilderTests` (1 site).

## Files new

| File | Purpose |
|------|---------|
| `src/PeakCan.Host.App/Services/Trace/ITraceSessionRegistry.cs` | Registry interface (Sources / LoadAsync / UnloadAsync / GetFrames / GetService / SourcesChanged) |
| `src/PeakCan.Host.App/Services/Trace/TraceSessionRegistry.cs` | Default impl — owns N per-load `ITraceViewerService` instances; defensively deep-copies frames at the boundary; palette slot assigned AFTER parse success (HIGH review fix) |
| `src/PeakCan.Host.App/Services/Trace/ITracePalette.cs` | Palette interface (deterministic per-sourceId color) |
| `src/PeakCan.Host.App/Services/Trace/TableauPalette.cs` | Default 10-color Tableau palette, hard-caps at capacity with `InvalidOperationException` |
| `src/PeakCan.Host.App/Services/Trace/TraceSource.cs` | `sealed record` (SourceId, DisplayName, Path, Color) |
| `src/PeakCan.Host.App/Composition/Converters/BooleanToVisibilityConverter.cs` | `bool → Visibility` |
| `src/PeakCan.Host.App/Composition/Converters/OxyColorToBrushConverter.cs` | `OxyColor → SolidColorBrush` (with 10-entry brush cache) |
| `tests/PeakCan.Host.App.Tests/Services/Trace/TraceSessionRegistryTests.cs` | 7 tests (6 originals + 1 HIGH review regression test) |
| `tests/PeakCan.Host.App.Tests/Services/Trace/TableauPaletteTests.cs` | 3 tests |
| `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelMultiTraceTests.cs` | 7 tests |

## Architecture (post v3.2.0)

```
[Load .asc #1] ─┐
[Load .asc #2] ─┼─→ TraceSessionRegistry ─→ N× TraceViewerService (per-load disposable)
[Load .asc #3] ─┘         │
                         Sources: List<TraceSource> (read-through)
                         SourcesChanged event
                              ↓
                    TraceViewerViewModel
                              ↓
                    AddTraceCommand / RemoveTraceCommand
                              ↓
                    XAML toolbar (Add trace… / Load DBC… / playback)
                              ↓
                    XAML legend strip (per-source swatch + ✕)
                              ↓
                    Chart subplots (per-signal PlotView, same SignalKey from
                                    different sources rendered as multiple
                                    LineSeries on the shared PlotModel)
```

## Pre-ship review

**Code-reviewer (sonnet)**: 0C / **1H** / 6M / 4L — **NEEDS FIX before ship**.

**HIGH fix applied**: `TraceSessionRegistry.LoadAsync` previously assigned
a palette slot BEFORE the ASC parse, burning the slot on parse failure
(self-inflicted capacity limit). Now:

1. Allocate `sourceId` + create the per-load `TraceViewerService`
2. `await service.LoadAsync(path, ct)` — parse runs first
3. If parse throws: propagate; palette slot is NOT burned
4. If parse succeeds: assign palette slot
5. If palette throws (capacity): dispose the freshly-loaded service
   then re-throw — no leak

Regression test added: `LoadAsync_NonexistentPath_DoesNotBurnPaletteSlot`
pins the contract.

**MEDIUM fixes applied**:
- Deleted dead `OnOpenAscClick` XAML handler (XAML now wires
  `OnAddTraceClick` directly)
- Deleted no-op `ChartViewModel_SeriesAreSourcedFromRegistry_OnRebuild`
  test (claimed to pin a contract it did not exercise)

**MEDIUM/LOW deferred to v3.3.0 or follow-up Tidy**:
- `SourcesChanged` thread-affinity documentation
- `ChartViewModel.Series.Clear()` is intentional (no repopulation
  in VM; chart series construction lives in the chart builder path)
- `OxyColorToBrushConverter` cache key equality relies on
  `OxyColor`'s default struct equality (verified working in practice)
- `TraceChartViewModel.Palette` dead array (kept for backward-compat
  with any external consumers; v3.3.0 will extract the SignalChartViewModel
  duplication too)

**Final verdict after HIGH + MEDIUM fixes**: APPROVE ship-as-is.

## Test count

| Suite | v3.1.1 | v3.2.0 | Δ |
|-------|--------|--------|---|
| App | 541 | **558** | **+17** (3 palette + 7 registry + 7 multi-trace VM) |
| Core | 393 | 393 | 0 |
| Infrastructure | 84 | 84 | 0 |
| **Total** | **1018 + 6 SKIP** | **1035 + 6 SKIP** | **+17 net** |

Note: plan estimated +10; actual +17. The 7-test delta comes from:
- 1 regression test added by the HIGH review fix
  (`LoadAsync_NonexistentPath_DoesNotBurnPaletteSlot`)
- 2 extra multi-trace VM tests written beyond the planned 5
  (added `MasterSourceId_DefaultsToFirstSource_WhenMultipleLoaded` and
  `Constructor_SubscribesToRegistrySourcesChanged` for coverage)
- 1 extra palette test (`PickColorFor_PastCapacity10_ThrowsInvalidOperationException`)

Pre-ship full suite: **1035 + 6 SKIP / 0 fail** (race flake did not
fire on the final run; documented pre-existing race flake pattern
(`CyclicSendServiceRaceTests` / `CyclicDbcSendServiceRaceTests`)
continues to pass in isolation per MEMORY).

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| Sync playback across N traces (master/non-master, proportional seek) | v3.3.0 — proportional seek math + master-source dropdown UI |
| `.tmtrace` bundle file format (save/restore multi-trace session) | v3.3.0 — JSON Schema + file dialog + 4 new commands |
| Palette exhaustion at 11+ (hash-based color fallback) | v3.3.0 — `TableauPalette` capacity throw is the v3.2.0 hard cap |
| Stroke style differentiation (solid/dashed) for color-blind accessibility | v3.3.0 — visual accessibility |
| Cross-source Y-axis auto-scale coordination | v3.3.0 — OxyPlot coordination across `PlotModel`s |
| `TraceChartViewModel.Palette` dead array extraction (consolidate into `TableauPalette`) | v3.3.0 — extract from both `TraceChartViewModel` + `SignalChartViewModel` |
| v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete | 42nd consecutive list; crypto review needed |
| v2.2 🔜 ODX CONDITIONAL / ECU-VARIANT | Implementation still pending |

## Process notes

- **Branch:** `feature/v3-2-trace-overlay` (1 commit on top of v3.1.1
  squash `52b86dfd`).
- **Pre-ship review:** code-reviewer (sonnet) — 0C / 1H / 6M / 4L
  initial. HIGH + 2 MEDIUMs fixed before ship. Final: APPROVE.
- **Build verification:** `dotnet build PeakCan.Host.slnx -c Debug`
  → 0 warnings, 0 errors.
- **Test verification:** `dotnet test PeakCan.Host.slnx`
  → 1035 + 6 SKIP / 0 fail (1 transient race-flake hit during pre-ship,
  passes in isolation per documented pattern).
- **Ship mechanism:** Tier 3 (`tier3_v320.py` — clone of `tier3_v311.py`,
  PARENT_SHA `52b86dfd`, 18 file overlays).
- **0 NEW lessons.** Pure feature delivery + clean DRY architecture.