# v3.0.2 PATCH — Subplot focus mode + adaptive height (2026-07-03)

## Summary

Closes the v3.0.0 V1 UI stub where subplots had a fixed 160 px height
and no focus/collapse controls. Subplots in the chart panel now size
themselves based on the chart area's actual height (`ChartAreaHeight`)
combined with each subplot's `IsFocused` / `IsCollapsed` flag. Each
subplot header now shows two inline buttons (`[Focus]` and `[▼ Collapse]`)
that drive the algorithm. The Trace Viewer stays a *non-bus*, read-only
playback window — no new public surface, no bus-touching changes, no
new dependencies.

This PATCH is a focused UI close-out. Five files, +246/−5 net.

## UI changes

- Per-subplot header row in the right-hand `ItemsControl` now shows two
  inline control buttons alongside the existing `DisplayName` label:
  - `[Focus]` — calls `vm.ChartViewModel.SetFocus(row)`. Clicking a
    second subplot's `[Focus]` transfers focus to it (single-focus).
    Clicking the focused subplot's `[Focus]` does **not** undo; use
    collapse instead (matches spec §3).
  - `[▼ Collapse]` — calls `vm.ChartViewModel.ToggleCollapse(row)`.
    Collapsed subplot measures 24 px (header only); another click
    re-expands it.
- `PlotView.Height` is now bound to `TraceChartSeries.AdaptiveHeight`
  (replaces the v3.0.0 hardcoded `Height="160"`).
- Effective heights (spec §3 algorithm):
  - Collapsed → 24 px (fixed).
  - Focused → `clamp(H · 0.5, 80, 250)`.
  - Non-focused in focus mode → `clamp((H · 0.5) / max(N − 1, 1), 80, 250)`.
  - General (no focus) → `clamp(H / N, 80, 250)`.
  where `N` is the count of non-collapsed series and `H` is the live
  `ChartAreaHeight`.

## Architecture

- `TraceChartSeries.AdaptiveHeight` (new init-only field, default
  `160.0`). Lives next to the existing `IsFocused` / `IsCollapsed`
  flags; updated through the `with`-expression so `ObservableCollection`
  raises `CollectionChanged.Replace` on every recompute.
- `TraceChartViewModel.ChartAreaHeight` (new CLR property). The setter
  guards on `SetProperty` (no-op when unchanged) and triggers
  `RecomputeHeights()` on every change.
- `TraceChartViewModel.Compute(...)` (new internal static) — pure
  function, single source of truth for the spec §3 algorithm. Six unit
  tests pin every branch (collapsed / focused / others-in-focus /
  general / upper-clamp / lower-clamp).
- `TraceChartViewModel.RecomputeHeights()` (new) — iterates `Series`,
  computes the new height per row, and only writes back when the
  numeric value actually changed (idempotent guard — prevents WPF
  binding churn during window resize or `Loaded` re-fire).
- `TraceViewerView.xaml` — header row + `PlotView` binding change +
  `x:Name="ChartScroll"` on the chart-area `ScrollViewer` with
  `Loaded` + `SizeChanged` hooks.
- `TraceViewerView.xaml.cs` — four forwarders:
  `OnFocusSubplotClick`, `OnCollapseSubplotClick`,
  `OnChartScrollLoaded`, `OnChartScrollSizeChanged`. All cast
  `sender.DataContext` to `TraceChartSeries` and the window's
  `DataContext` to `TraceViewerViewModel`, then call into
  `vm.ChartViewModel`.

## Test delta

| Suite         | v3.0.1 | v3.0.2 | Δ                                            |
|---------------|--------|--------|----------------------------------------------|
| App           | 514    | 520    | **+6** (`TraceChartViewModelTests`: algorithm) |
| Core          | 393    | 393    | 0                                            |
| Infrastructure | 84     | 84     | 0                                            |
| **Total**     | **991 + 6 SKIP** | **997 + 6 SKIP** | **+6 net**                |

`TraceChartViewModelTests` is now **13 total** (7 pre-existing +
6 v3.0.2 algorithm tests). Full suite + 30/30 race-test confirmation
gate expected to remain GREEN; see Tier 3 CI step.

## Files modified

- `src/PeakCan.Host.App/ViewModels/TraceChartSeries.cs`
  (+`AdaptiveHeight` init-only field, default 160.0)
- `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs`
  (+`ChartAreaHeight` property, `Compute(...)` static, `RecomputeHeights()`;
  ~+89/−1)
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml`
  (+header buttons, `Loaded`/`SizeChanged` hooks, `Height` binding; ~+8/−3)
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs`
  (+4 forwarders; +45/−0)
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs`
  (+6 algorithm tests; +94/−0)

## Lessons

Three lessons surfaced during Tasks 1–3; all are inline, all are
reusable.

1. **`ObservableCollection.SetItem` raises `CollectionChanged.Replace`**
   — when an item is replaced via `Series[i] = cur with { ... }`,
   WPF's row `DataContext` is rebound automatically. This is why the
   algorithm can update `AdaptiveHeight` on an existing row without
   requiring `INotifyPropertyChanged` on `TraceChartSeries`. **When a
   `record` is held in an `ObservableCollection<T>` and T does not
   implement `INotifyPropertyChanged`, replace via index assignment
   (not `.Add` then `.Remove`) so the row's `DataContext` rebinds.**

2. **Idempotent recompute with a numeric guard prevents WPF binding
   churn** — `RecomputeHeights()` only assigns `Series[i] = cur with
   { AdaptiveHeight = newHeight }` when the new value actually differs
   from the current one. During a window drag or `Loaded` re-fire the
   algorithm can produce the same value repeatedly; without the guard
   every recompute would replace the row and trigger a `DataContext`
   rebind on every subplot. **When a property's setter triggers a
   batch update, guard the write on `current != next` to keep
   `ObservableCollection` change-notifications lazy.**

3. **`Loaded` + `SizeChanged` covers both first-paint and window-resize**
   — `Loaded` fires exactly once (first render) and is the only chance
   to read a non-zero `ActualHeight` before the user touches anything.
   `SizeChanged` fires on every subsequent size change (window resize,
   `GridSplitter` drag, DPI change). Pairing both is the standard WPF
   idiom for "keep an external model in sync with the rendered size of
   a `FrameworkElement`". **For a `FrameworkElement` whose
   `ActualHeight` needs to feed into a non-UI model, wire BOTH
   `Loaded` and `SizeChanged` and forward to the same setter.**

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| Click-focus-again-to-undo | Spec §3 explicitly excludes; collapse is the only "hide" path |
| Per-subplot custom-pixel user override | Out of scope per spec §2 Non-goals |
| Multi-trace comparison / diff | Out of scope per spec §2 Non-goals |
| Bookmarks / annotations | Out of scope per spec §2 Non-goals |
| PNG export | CSV only in V1 per spec §2 Non-goals |
| DBC value-table encoding in Trace Viewer | Long-term non-goal since v1.4.0 |

## Process notes

- **Branch:** `feature/v3-0-2-patch` (2 commits: 3282bcf + d85c51e).
- **Pre-ship review:** Task 3 — 0C / 0H / 5M, all Minor wording/
  cosmetic; none required post-review code edits.
- **Test isolation:** all 6 new tests are STA-safe; they run in the
  standard Core/App test pipelines.
- **Ship mechanism:** Tier 3 force-update via `gh api` (parent SHA
  → tree → blobs → tree → commit → refs/tags → release). Pattern
  established in v3.0.0 + v3.0.1 ships.
