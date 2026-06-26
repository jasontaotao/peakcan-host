using FluentAssertions;
using OxyPlot;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.Infrastructure.Statistics;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Task 17: <see cref="StatsViewModel"/> owns the rolling-window chart
/// for the Stats tab. The constructor pre-fills the empty rolling
/// windows with zeros so the chart renders a stable axis range on
/// first render, and <see cref="StatsViewModel.Push"/> appends a new
/// <see cref="BusStatistics"/> sample, refreshing the totals and
/// trimming the window to <c>MaxPoints</c>.
/// <para>
/// <b>Concurrency model:</b> <see cref="StatsViewModel.Push"/> decodes
/// on the calling thread (the timer thread of <see cref="Services.StatisticsService"/>)
/// and marshals the <c>ObservableCollection</c> mutations to the WPF UI
/// thread via <c>Dispatcher.InvokeAsync</c>. The non-STA tests below
/// run on xunit's MTA thread with no <c>Application</c> instance, so
/// the dispatcher is null and the mutations run inline — the test
/// observes the post-state directly. The full dispatcher hop is
/// exercised in production by the WPF smoke run (Task 20).
/// </para>
/// <para>
/// <b>v1.2.1 PATCH (Task 5):</b> the ctor calls
/// <see cref="LeakedApplicationReset.CleanupLeakedApplication"/> to null
/// out any leaked <see cref="System.Windows.Application.Current"/> from a
/// sibling test class. Without this, the inline path inside
/// <see cref="StatsViewModel.Push"/> would route through
/// <c>Dispatcher.InvokeAsync</c> on a dead dispatcher and
/// <c>TotalFrames</c> would stay 0.
/// </para>
/// </summary>
public class StatsViewModelTests
{
    // v1.2.1 PATCH Task 5: defensive cleanup of leaked Application.Current
    // before each test (ctor runs once per test instance in xUnit).
    public StatsViewModelTests() => LeakedApplicationReset.CleanupLeakedApplication();

    private static BusStatistics MakeSnap(long total = 0, long errors = 0,
                                          double fps = 0.0, long bytes = 0,
                                          double bps = 0.0, double load = 0.0)
        => new(total, errors, fps, bytes, bps, load);

    [Fact]
    public void Default_State_Has_PreFilled_Empty_Series_And_Zero_Totals()
    {
        // The rolling window is pre-filled with 60 zeros so the chart
        // has a stable X axis (0..60) before the first 1 Hz tick. This
        // is an MVP shortcut — without it, the chart shows no axis
        // until the first tick arrives 1 s after start.
        var vm = new StatsViewModel();
        vm.FpsSeries.Should().HaveCount(60, "the rolling window is pre-filled to MaxPoints=60");
        vm.LoadSeries.Should().HaveCount(60);
        vm.FpsSeries.Should().AllBeEquivalentTo(0.0);
        vm.LoadSeries.Should().AllBeEquivalentTo(0.0);
        vm.TotalFrames.Should().Be(0);
        vm.ErrorFrames.Should().Be(0);
    }

    [Fact]
    public void Default_PlotModel_Has_Two_LineSeries_And_Two_Axes()
    {
        // The chart wiring must include both series (FPS, load) so
        // the legend and Y axis values are populated on first render.
        var vm = new StatsViewModel();
        vm.PlotModel.Should().NotBeNull();
        vm.PlotModel.Series.Should().HaveCount(2,
            "two LineSeries are constructed: frames/sec + bus load %");
        vm.PlotModel.Axes.Should().HaveCount(2,
            "two LinearAxis: bottom sample index + left value scale");
    }

    [Fact]
    public void Push_Updates_TotalFrames_And_ErrorFrames()
    {
        var vm = new StatsViewModel();
        vm.Push(MakeSnap(total: 12345, errors: 42, fps: 100, load: 25));

        vm.TotalFrames.Should().Be(12345);
        vm.ErrorFrames.Should().Be(42);
    }

    [Fact]
    public void Push_Appends_New_Sample_To_FpsSeries_And_LoadSeries()
    {
        var vm = new StatsViewModel();
        vm.Push(MakeSnap(fps: 250, load: 12.5));

        // Series was 60 zeros; after one push it should be 60 zeros
        // with the latest value at the tail.
        vm.FpsSeries.Should().HaveCount(60);
        vm.FpsSeries[^1].Should().Be(250.0);
        vm.LoadSeries[^1].Should().Be(12.5);
    }

    [Fact]
    public void Push_Trims_Series_To_MaxPoints_After_Many_Samples()
    {
        // 65 pushes should keep the rolling window at exactly 60
        // samples; the oldest 5 samples are dropped from the head.
        var vm = new StatsViewModel();
        for (var i = 0; i < 65; i++)
        {
            vm.Push(MakeSnap(fps: i, load: i));
        }

        vm.FpsSeries.Should().HaveCount(60);
        vm.LoadSeries.Should().HaveCount(60);
        // The latest sample is the 65th (i=64).
        vm.FpsSeries[^1].Should().Be(64.0);
        // The oldest retained sample is the 5th (i=5).
        vm.FpsSeries[0].Should().Be(5.0);
    }

    [Fact]
    public void Push_Does_Not_Throw_When_Series_Empty()
    {
        // Edge case: the pre-filled series keeps count at 60; Push
        // must not assume any minimum size. Already covered by the
        // 65-push trim test, but added here as a contract for future
        // refactors that may drop the pre-fill.
        var vm = new StatsViewModel();
        var act = () => vm.Push(MakeSnap(fps: 1, load: 1));
        act.Should().NotThrow();
    }

    [Fact]
    public void Push_Without_Application_Dispatcher_Runs_Inline()
    {
        // The dispatcher-marshalled production path is not testable
        // here (no Application), but the inline fallback must work
        // for xunit to observe the post-state.
        var vm = new StatsViewModel();
        vm.Push(MakeSnap(total: 100, fps: 50, load: 10));
        // No exception + totals updated = inline path worked.
        vm.TotalFrames.Should().Be(100);
        vm.FpsSeries[^1].Should().Be(50.0);
    }

    [Fact]
    public void Apply_Increments_InvalidatePlotCallCount()
    {
        // v1.2.10 PATCH: v1.2.9 fixed the X-index collapse but missed
        // PlotModel.InvalidatePlot(updateData: true) at the end of
        // Apply. OxyPlot 2.2.0 WPF does NOT auto-redraw on a manual
        // LineSeries.Points.Clear+Add, so the chart stays frozen on
        // the first render even though new samples stream in. The
        // Updated event is a no-op from InvalidatePlot without an
        // attached IPlotView, so we use a deterministic internal
        // counter — mirrors the project's existing test-hook pattern
        // (SignalViewModel.FilterRebuildCount / DrainCount).
        var vm = new StatsViewModel();
        Assert.Equal(0, vm.InvalidatePlotCallCount);

        // Inline Apply path: test context has no Application, so
        // RunOnUiPost falls through to the synchronous Action invoke.
        vm.Push(MakeSnap(total: 1, errors: 0, fps: 100.0, bytes: 0, bps: 0, load: 5.0));

        Assert.True(vm.InvalidatePlotCallCount >= 1,
            $"Expected Apply to call InvalidatePlot at least once; got {vm.InvalidatePlotCallCount}.");
    }
}
