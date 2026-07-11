using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class SignalViewModel
{
    // Flow C: Chart plotting (v3.16.x + earlier).
    // Methods moved verbatim from SignalViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - All 4 methods -> _chartVm (DI field, main file)
    //   - PlotAll + ClearChart + PlotNone -> Latest (state, main file)
    //
    // [RelayCommand] attributes MUST travel with their methods.

    /// <summary>
    /// Export charted signal data to CSV. Prompts for file path via
    /// <see cref="SaveFileDialog"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasChartedSignals))]
    private void ExportChartCsv()
    {
        if (_chartVm is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = "signal-chart.csv",
        };
        if (dlg.ShowDialog() == true)
        {
            _chartVm.ExportToCsv(dlg.FileName);
        }
    }

    /// <summary>
    /// Clear all charted signals and reset the chart.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasChartedSignals))]
    private void ClearChart()
    {
        if (_chartVm is null) return;
        // Uncheck all IsSelected flags in the grid.
        foreach (var entry in Latest)
            entry.IsSelected = false;
        _chartVm.Reset();
    }

    /// <summary>Select all signals for charting.</summary>
    [RelayCommand]
    private void PlotAll()
    {
        if (_chartVm is null) return;
        foreach (var entry in Latest)
        {
            if (!entry.IsSelected)
            {
                entry.IsSelected = true;
                _chartVm.AddSignal($"{entry.Message}.{entry.Signal}", entry.Signal);
            }
        }
    }

    /// <summary>Deselect all signals and clear the chart.</summary>
    [RelayCommand]
    private void PlotNone()
    {
        if (_chartVm is null) return;
        foreach (var entry in Latest)
            entry.IsSelected = false;
        _chartVm.Reset();
    }
}