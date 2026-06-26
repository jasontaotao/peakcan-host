using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v1.2.10: regression tests for the Signal plot toggle Click handler.
/// Pre-fix the handler read sender.DataContext.IsSelected from the
/// row's SignalEntry — but DrainPending replaces Latest[i] every frame,
/// and the row's DataContext can target the NEW entry (whose IsSelected
/// was preserved=true from the OLD entry the user just unchecked). The
/// fix routes through HandlePlotCheckboxClick(entry, cb.IsChecked) so
/// the UI-side toggle value drives the AddSignal/RemoveSignal call.
/// </summary>
public sealed class SignalViewModelClickHandlerTests
{
    [Fact]
    public void HandlePlotCheckboxClick_True_Adds_Signal_To_Chart()
    {
        // Arrange: real chart VM + entry added to Latest
        var chartVm = new SignalChartViewModel();
        var vm = new SignalViewModel(chartVm);
        var entry = new SignalEntry { Message = "M2", Signal = "S2" };
        vm.Latest.Add(entry);

        // Act: handler invoked with cb.IsChecked = true (user just checked)
        vm.HandlePlotCheckboxClick(entry, isChecked: true);

        // Assert: chart VM now has 1 signal
        Assert.Equal(1, chartVm.SignalCount);
    }

    [Fact]
    public void HandlePlotCheckboxClick_False_After_Entry_Replacement_Removes_From_Chart()
    {
        // Arrange: real chart VM, signal added
        var chartVm = new SignalChartViewModel();
        var vm = new SignalViewModel(chartVm);
        var entry = new SignalEntry { Message = "M1", Signal = "S1" };
        vm.Latest.Add(entry);

        // First: user checked → AddSignal called → chart has 1
        vm.HandlePlotCheckboxClick(entry, isChecked: true);
        Assert.Equal(1, chartVm.SignalCount);

        // Simulate DrainPending's Upsert replacing Latest[i]:
        //   entry.IsSelected = Latest[i].IsSelected (=true)  ← preserve
        //   Latest[i] = newEntry                              ← replace
        // The NEW entry has IsSelected=true (preserved from OLD).
        var newEntry = new SignalEntry
        {
            Message = "M1",
            Signal = "S1",
            IsSelected = true,
        };
        vm.Latest[0] = newEntry;

        // Act: user clicks to UNCHECK (cb.IsChecked = false).
        // Pre-fix: handler read entry.IsSelected = true → AddSignal called (wrong)
        // Post-fix: handler reads cb.IsChecked = false → RemoveSignal called (correct)
        vm.HandlePlotCheckboxClick(newEntry, isChecked: false);

        // Assert: chart VM signal count is 0 (line removed)
        Assert.Equal(0, chartVm.SignalCount);
    }

    [Fact]
    public void HandlePlotCheckboxClick_False_On_Never_Added_Signal_Is_Noop()
    {
        // Arrange: chart VM exists but signal never added
        var chartVm = new SignalChartViewModel();
        var vm = new SignalViewModel(chartVm);
        var entry = new SignalEntry { Message = "M3", Signal = "S3" };
        vm.Latest.Add(entry);

        // Act: user unchecks without ever checking first
        vm.HandlePlotCheckboxClick(entry, isChecked: false);

        // Assert: no exception, chart still empty
        Assert.Equal(0, chartVm.SignalCount);
    }
}
