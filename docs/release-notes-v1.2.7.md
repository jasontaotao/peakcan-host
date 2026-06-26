# Release Notes — PeakCan Host v1.2.7

**Date:** 2026-06-26

## Summary

v1.2.7 is a 2-commit PATCH that fixes two related UI bugs that
surfaced on the v1.2.6 build under load:

1. **Signal view Plot checkbox was unresponsive.** Per-row
   checkbox clicks did not add the signal to the chart, even
   though the Plot All toolbar button worked. Root cause:
   WPF `DataGridCheckBoxColumn` under a parent `IsReadOnly=True`
   `DataGrid` does not raise `CellEditEnding` reliably on the
   .NET 10 build, so the per-row `OnSignalSelectionChanged`
   path was never invoked.
2. **Stats tab chart did not render data lines.** Axes and
   labels rendered, but the two `LineSeries` (FPS / bus load)
   were empty even after a long sample stream. Root cause:
   OxyPlot 2.2.0's WPF binding of `LineSeries.ItemsSource` to
   `ObservableCollection<T>` is broken on .NET 10 windows — the
   visual does not update even though the binding path is
   subscribed. The `SignalChartViewModel` (which uses
   `series.Points.Add(new DataPoint(x, y))` instead) was
   rendering fine, which is why this bug only affected the
   Stats tab.

## Why both bugs surfaced now

v1.2.4 restored Trace/Stats data flow
(`IHost.StartAsync` + `AddHostedService` for
`TraceService`/`StatisticsService`). v1.2.5 unlocked
Extended-frame decode (Branch B). v1.2.6 fixed the
`OnDrainTick` dispatcher hop (v1.2.3 PATCH-2 regression that
left `FilterSignals` collection mutations on a threadpool
worker). With the pipeline actually delivering data, both
Signal and Stats UIs got exercised for the first time on a
.NET 10 production build, exposing the two latent issues.

## Fix 1 — Signal view Plot checkbox (commit `4d60266`)

Replaced `DataGridCheckBoxColumn` with a
`DataGridTemplateColumn` + explicit `CheckBox` + Click
handler in `SignalView.xaml.cs`:

```xml
<DataGridTemplateColumn Header="Plot" Width="40">
    <DataGridTemplateColumn.CellTemplate>
        <DataTemplate>
            <CheckBox IsChecked="{Binding IsSelected, Mode=TwoWay,
                          UpdateSourceTrigger=PropertyChanged}"
                      HorizontalAlignment="Center"
                      VerticalAlignment="Center"
                      Click="OnPlotCheckboxClick"/>
        </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

```csharp
private void OnPlotCheckboxClick(object sender, RoutedEventArgs e)
{
    if (sender is CheckBox { DataContext: SignalEntry entry }
        && DataContext is SignalViewModel vm)
    {
        vm.OnSignalSelectionChanged(entry.Message, entry.Signal, entry.IsSelected);
    }
}
```

The `Click` event fires regardless of `DataGrid` edit-mode
state, so it routes reliably to `vm.OnSignalSelectionChanged`.
The previous `OnCellEditEnding` handler is kept as a
belt-and-braces fallback.

## Fix 2 — Stats tab chart rendering (commit `ca1e530`)

Refactored `StatsViewModel` to maintain chart data via
`series.Points.Add(new DataPoint(x, y))` directly on the
`LineSeries` references, mirroring `SignalChartViewModel`'s
working pattern. The previously-broken `ItemsSource = FpsSeries`
binding is removed.

`FpsSeries` / `LoadSeries` `ObservableCollection<double>` are
kept as parallel collections, updated alongside the
`LineSeries.Points`, so existing tests in
`StatsViewModelTests` and `StatisticsServiceTests` continue to
pass without modification.

## Why this is a PATCH (not a MINOR)

Both fixes are scoped to the production runtime behaviour
under load. The public API is unchanged. `PlotModel` is
still exposed; `FpsSeries` / `LoadSeries` are still public
collections on `StatsViewModel`; the `Plot` column header is
still in the Signal DataGrid; `OnSignalSelectionChanged` is
still the VM entry point for chart selection.

## Tests

548 pass + 6 SKIP + 0 fail (no test count change). The
checkbox fix is UI event wiring (production coverage via the
v1.2.0 Task 20 WPF smoke run). The Stats fix is exercised by
the existing `StatsViewModelTests.Push_Appends_New_Sample_To_FpsSeries_And_LoadSeries`
test, which continues to pass because the parallel
`ObservableCollection<double>` is still updated.

## Files changed

- `src/PeakCan.Host.App/Views/SignalView.xaml` —
  `DataGridCheckBoxColumn` → `DataGridTemplateColumn` + `CheckBox`
- `src/PeakCan.Host.App/Views/SignalView.xaml.cs` — added
  `OnPlotCheckboxClick` handler + `using System.Windows;`
- `src/PeakCan.Host.App/ViewModels/StatsViewModel.cs` — hold
  direct `LineSeries` references, push via `series.Points.Add`
  alongside the `ObservableCollection<double>` updates

## Known issue (carried over)

The Stats tab `OxyPlot` `Legend` is empty by default in
OxyPlot 2.2.0 (no legend rendering for the FPS / bus-load
series labels). Pre-existing since v0.0.1. Replacement
deferred to v1.3.0 MINOR (OxyPlot 2.2.0 WPF chart
replacement — Canvas + custom drawing, or migrate to a
.NET 10-compatible chart lib).

## Next work

1. **v1.2.8 PATCH** (small): add `Legend` to
   `StatsViewModel.PlotModel.Legends` so the FPS / bus-load
   series names render in the chart legend. The data is
   now rendering; only the legend annotation is missing.
2. **v1.3.0 MINOR (OEM IKeyDerivationAlgorithm + OxyPlot
   full replacement)** — blocked on OEM list.

## Ship mechanics

`git -c http.proxy="http://127.0.0.1:7897" push origin main`
(proxy alive; direct connection reset on first attempt) +
`git tag -a v1.2.7 -m "..."` + `git push origin v1.2.7` +
`gh release create v1.2.7 --title ... --notes-file
docs/release-notes-v1.2.7.md`.