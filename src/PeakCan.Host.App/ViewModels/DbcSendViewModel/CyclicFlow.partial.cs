using System.Globalization;
using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcSendViewModel
{
    /// <summary>
    /// v1.5.1 PATCH Item 2: start periodic DBC transmission on the
    /// selected message at <see cref="DbcCyclicIntervalText"/> ms.
    /// The frame provider supplies the current <see cref="SelectedDbcMessage"/>
    /// + per-signal values, so user edits to the SignalRows DataGrid
    /// flow into the periodic send path on the next tick (Decision 8).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartDbcCyclic))]
    private void StartDbcCyclic()
    {
        if (SelectedDbcMessage is null) return;
        // v1.5.1 PATCH Item 2: interval is MILLISECONDS (UI label says so),
        // not a TimeSpan string. TimeSpan.TryParse("100") returns 100 days,
        // which would silently make the periodic send a 100-day timer.
        // Mirror SendViewModel.cs:279-282 pattern: int.TryParse + bounds 1..60000.
        if (!int.TryParse(DbcCyclicIntervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            || ms < 1 || ms > 60_000)
        {
            ErrorMessage = $"Invalid interval: '{DbcCyclicIntervalText}' (must be 1..60000 ms)";
            return;
        }
        var interval = TimeSpan.FromMilliseconds(ms);
        _cyclicDbc.Start(
            () => (SelectedDbcMessage!, BuildCurrentSignalValues()),
            interval);
        IsDbcCyclicRunning = true;
    }

    /// <summary>v1.5.1 PATCH Item 2: stop the periodic DBC transmission.</summary>
    [RelayCommand(CanExecute = nameof(CanStopDbcCyclic))]
    private void StopDbcCyclic()
    {
        _cyclicDbc.Stop();
        IsDbcCyclicRunning = false;
    }
}