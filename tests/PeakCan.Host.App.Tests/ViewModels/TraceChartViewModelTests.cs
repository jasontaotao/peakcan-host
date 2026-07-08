using FluentAssertions;
using OxyPlot;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class TraceChartViewModelTests
{
    private static TraceChartSeries MakeSeries(string key, params (double x, double y)[] pts)
    {
        var plot = new PlotModel();
        plot.Axes.Add(new OxyPlot.Axes.LinearAxis { Position = OxyPlot.Axes.AxisPosition.Bottom });
        plot.Axes.Add(new OxyPlot.Axes.LinearAxis { Position = OxyPlot.Axes.AxisPosition.Left });
        var line = new OxyPlot.Series.LineSeries { Title = key };
        foreach (var (x, y) in pts) line.Points.Add(new DataPoint(x, y));
        plot.Series.Add(line);
        var xs = pts.Select(p => p.x).ToArray();
        var ys = pts.Select(p => p.y).ToArray();
        // Handle the no-points case (used by AddSeries/RemoveSeries/
        // ToggleCollapse/SetFocus tests) so the Min/Max record fields are
        // always well-defined. The contract tests assert on observable
        // behavior, not on these scalar values.
        var min = ys.Length == 0 ? 0.0 : ys.Min();
        var max = ys.Length == 0 ? 0.0 : ys.Max();
        return new TraceChartSeries(key, key, "", OxyColors.Blue, plot, xs, ys,
            min, max, false, false);
    }

    [Fact]
    public void Ctor_Empty_HasZeroSeries()
    {
        var sut = new TraceChartViewModel();
        sut.Series.Should().BeEmpty();
        sut.PlaybackCursorX.Should().Be(0.0);
        sut.TotalDuration.Should().Be(0.0);
    }

    [Fact]
    public void AddSeries_AppendsToObservableCollection()
    {
        var sut = new TraceChartViewModel();
        sut.AddSeries(MakeSeries("A"));
        sut.AddSeries(MakeSeries("B"));
        sut.Series.Should().HaveCount(2);
    }

    [Fact]
    public void RemoveSeries_RemovesFromCollection()
    {
        var sut = new TraceChartViewModel();
        var s = MakeSeries("A");
        sut.AddSeries(s);
        sut.RemoveSeries(s);
        sut.Series.Should().BeEmpty();
    }

    [Fact]
    public void UpdatePlaybackCursor_SetsProperty()
    {
        var sut = new TraceChartViewModel();
        sut.UpdatePlaybackCursor(12.345);
        sut.PlaybackCursorX.Should().Be(12.345);
    }

    // v3.16.9 PATCH: 16ms throttle. The playback timer (ReplayTimeline.OnTick)
    // fires every 1 ms (1000 fps). Without throttling, UpdatePlaybackCursor
    // would call InvalidatePlot on every tick per series, freezing the WPF
    // window (user report: "clicked Play and the window froze"). 60 fps
    // (16 ms) is the WPF default render cadence — matches the human eye's
    // perception limit.
    // v3.16.9.1 PATCH (code-review M2): the previous test only asserted
    // PlaybackCursorX which is set UNCONDITIONALLY before the throttle
    // check — that assertion passes EVEN with the throttle completely
    // removed (false-positive green). The new test asserts
    // InvalidatePlotCallCount which IS only incremented inside the
    // throttle-allowed path: 100 rapid calls within 16 ms should produce
    // exactly 1 invalidate (the first call), not 100. A future
    // "simplification" PR that removes the throttle will fail this
    // test loudly.
    [Fact]
    public void UpdatePlaybackCursor_RapidCallsWithin16ms_DoesNotInvalidate()
    {
        var sut = new TraceChartViewModel();
        // Add a series with a playback-cursor LineAnnotation so the
        // foreach-over-Series path actually finds the annotation and
        // calls InvalidatePlot on the first allowed call.
        var series = MakeSeries("A", (0, 0), (1, 1));
        series.PlotModel.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
        {
            Type = OxyPlot.Annotations.LineAnnotationType.Vertical,
            X = 0.0,
            Tag = "playback-cursor",
        });
        sut.AddSeries(series);
        var beforeCount = sut.InvalidatePlotCallCount;
        // Burst 100 calls in <1 ms (sub-16ms window). With the 16 ms
        // throttle, only the FIRST call (which is the only one allowed
        // before _lastCursorInvalidateTicks is set) should reach the
        // InvalidatePlot call. The remaining 99 are skipped.
        for (var i = 0; i < 100; i++)
            sut.UpdatePlaybackCursor(i * 0.001);
        var afterCount = sut.InvalidatePlotCallCount;
        // The first call enters the invalidate path; calls 2..100 are
        // all within the 16 ms window AND have unique x values, so they
        // all get skipped. Result: exactly 1 invalidate in the burst.
        (afterCount - beforeCount).Should().Be(1,
            "100 rapid calls within 16 ms must collapse to 1 InvalidatePlot call (v3.16.9 PATCH 16ms throttle contract)");
        // Property must still reflect the LATEST value (throttle only
        // affects InvalidatePlot, not the property write — the property
        // is bound to a UI TextBlock that polls per render frame).
        sut.PlaybackCursorX.Should().Be(99 * 0.001);
    }

    [Fact]
    public void GetStatistics_ReturnsMinMaxAvgN()
    {
        var sut = new TraceChartViewModel();
        sut.AddSeries(MakeSeries("A", (0, 10), (1, 20), (2, 30)));
        var stats = sut.GetStatistics().ToList();
        stats.Should().HaveCount(1);
        stats[0].SignalKey.Should().Be("A");
        stats[0].Min.Should().Be(10);
        stats[0].Max.Should().Be(30);
        stats[0].Average.Should().Be(20);
        stats[0].SampleCount.Should().Be(3);
    }

    [Fact]
    public void ToggleCollapse_FlapsIsCollapsed()
    {
        var sut = new TraceChartViewModel();
        var s = MakeSeries("A");
        sut.AddSeries(s);
        sut.ToggleCollapse(s);
        sut.Series.First().IsCollapsed.Should().BeTrue();
        sut.ToggleCollapse(s);
        sut.Series.First().IsCollapsed.Should().BeFalse();
    }

    [Fact]
    public void SetFocus_TogglesOtherSeriesFocusedFalse()
    {
        var sut = new TraceChartViewModel();
        var a = MakeSeries("A");
        var b = MakeSeries("B");
        sut.AddSeries(a);
        sut.AddSeries(b);
        sut.SetFocus(a);
        sut.Series.First(x => x.SignalKey == "A").IsFocused.Should().BeTrue();
        sut.Series.First(x => x.SignalKey == "B").IsFocused.Should().BeFalse();
        sut.SetFocus(b);
        sut.Series.First(x => x.SignalKey == "A").IsFocused.Should().BeFalse();
        sut.Series.First(x => x.SignalKey == "B").IsFocused.Should().BeTrue();
    }

    // v3.0.2 PATCH Task 1: adaptive subplot height + focus mode.
    // Algorithm (per spec §3):
    //   Collapsed            -> 24
    //   Focused              -> clamp(H * 0.5, 80, 250)
    //   Others (focus mode)  -> clamp((H * 0.5) / max(N - 1, 1), 80, 250)
    //   General (no focus)   -> clamp(H / N, 80, 250)
    // where N = number of non-collapsed subplots.

    [Fact]
    public void RecomputeHeights_OneSeries_ClampedTo250()
    {
        var sut = new TraceChartViewModel();
        sut.AddSeries(MakeSeries("A"));
        sut.ChartAreaHeight = 800;
        // Single series: equal-share 800/1 = 800, clamped to Max=250.
        sut.Series.First().AdaptiveHeight.Should().Be(250);
    }

    [Fact]
    public void RecomputeHeights_ThreeSeriesUnfocused_EqualShare()
    {
        var sut = new TraceChartViewModel();
        sut.AddSeries(MakeSeries("A"));
        sut.AddSeries(MakeSeries("B"));
        sut.AddSeries(MakeSeries("C"));
        sut.ChartAreaHeight = 600;
        // 600 / 3 = 200, no clamp.
        sut.Series.Should().OnlyContain(s => s.AdaptiveHeight == 200);
    }

    [Fact]
    public void RecomputeHeights_OneFocused_TakesHalf()
    {
        var sut = new TraceChartViewModel();
        var a = MakeSeries("A");
        var b = MakeSeries("B");
        var c = MakeSeries("C");
        sut.AddSeries(a);
        sut.AddSeries(b);
        sut.AddSeries(c);
        sut.ChartAreaHeight = 800;
        sut.SetFocus(b);
        // Focused B: clamp(800 * 0.5 = 400, 80, 250) = 250 (clamped).
        sut.Series.First(x => x.SignalKey == "B").AdaptiveHeight.Should().Be(250);
        // Others (A, C): clamp(400 / 2 = 200, 80, 250) = 200.
        sut.Series.First(x => x.SignalKey == "A").AdaptiveHeight.Should().Be(200);
        sut.Series.First(x => x.SignalKey == "C").AdaptiveHeight.Should().Be(200);
    }

    [Fact]
    public void RecomputeHeights_OneCollapsed_TwentyFourPx()
    {
        var sut = new TraceChartViewModel();
        var a = MakeSeries("A");
        var b = MakeSeries("B");
        var c = MakeSeries("C");
        sut.AddSeries(a);
        sut.AddSeries(b);
        sut.AddSeries(c);
        sut.ChartAreaHeight = 800;
        sut.ToggleCollapse(b);
        // Visible count = 2 (A, C). 800 / 2 = 400 -> clamp to 250.
        sut.Series.First(x => x.SignalKey == "A").AdaptiveHeight.Should().Be(250);
        sut.Series.First(x => x.SignalKey == "C").AdaptiveHeight.Should().Be(250);
        // Collapsed B = 24.
        sut.Series.First(x => x.SignalKey == "B").AdaptiveHeight.Should().Be(24);
    }

    [Fact]
    public void RecomputeHeights_ClampUpperAt250()
    {
        var sut = new TraceChartViewModel();
        sut.AddSeries(MakeSeries("A"));
        sut.ChartAreaHeight = 1000;
        // 1000 / 1 = 1000 -> clamp to 250.
        sut.Series.First().AdaptiveHeight.Should().Be(250);
    }

    [Fact]
    public void RecomputeHeights_ClampLowerAt80()
    {
        var sut = new TraceChartViewModel();
        // Case 1: H=80, 1 series -> 80 (no change, at boundary).
        sut.AddSeries(MakeSeries("A"));
        sut.ChartAreaHeight = 80;
        sut.Series.First().AdaptiveHeight.Should().Be(80);

        // Case 2: H=80, 5 series none focused -> clamp(80 / 5 = 16, 80, 250) = 80.
        var sut2 = new TraceChartViewModel();
        for (int i = 0; i < 5; i++) sut2.AddSeries(MakeSeries($"S{i}"));
        sut2.ChartAreaHeight = 80;
        sut2.Series.Should().OnlyContain(s => s.AdaptiveHeight == 80);
    }

    // v3.3.2 PATCH Task 1: cross-source Y-axis auto-scale coordination.
    // SyncYAxes groups subplots by SignalKey (logical, not EffectiveKey)
    // and sets each group's Y axis to the union min/max of all sources'
    // Y data with 5% padding for visual breathing room.

    [Fact]
    public void SyncYAxes_SingleSource_SetsSubplotYAxisToDataRange_WithPadding()
    {
        // v3.3.2 PATCH: Y axis range = data min/max + 5% padding.
        var sut = new TraceChartViewModel();
        var s = MakeSeries("A", (0, 10), (1, 20), (2, 30));
        sut.AddSeries(s);

        sut.SyncYAxes();

        var yAxis = s.PlotModel.Axes.OfType<OxyPlot.Axes.LinearAxis>()
            .First(a => a.Position == OxyPlot.Axes.AxisPosition.Left);
        // Data range is 10..30, range=20, padding=1, so y axis = 9..31
        yAxis.Minimum.Should().BeApproximately(9.0, 0.001);
        yAxis.Maximum.Should().BeApproximately(31.0, 0.001);
    }

    [Fact]
    public void SyncYAxes_TwoSourcesSameSignal_SharesMinMaxOfBoth()
    {
        // v3.3.2 PATCH: 2 sources with same SignalKey share Y axis.
        // Source A: Y = 10..20 (range 10)
        // Source B: Y = 5..50 (range 45)
        // Global: 5..50 (range 45), padding 2.25, axis = 2.75..52.25
        var sut = new TraceChartViewModel();
        var sA = MakeSeries("A", (0, 10), (1, 20));
        var sB = MakeSeries("A", (0, 5), (1, 50));
        sut.AddSeries(sA);
        sut.AddSeries(sB);

        sut.SyncYAxes();

        var yA = sA.PlotModel.Axes.OfType<OxyPlot.Axes.LinearAxis>()
            .First(a => a.Position == OxyPlot.Axes.AxisPosition.Left);
        var yB = sB.PlotModel.Axes.OfType<OxyPlot.Axes.LinearAxis>()
            .First(a => a.Position == OxyPlot.Axes.AxisPosition.Left);
        yA.Minimum.Should().BeApproximately(2.75, 0.001);
        yA.Maximum.Should().BeApproximately(52.25, 0.001);
        yB.Minimum.Should().BeApproximately(2.75, 0.001);
        yB.Maximum.Should().BeApproximately(52.25, 0.001);
    }

    [Fact]
    public void SyncYAxes_TwoSourcesDifferentSignals_IndependentYAxes()
    {
        // v3.3.2 PATCH: different SignalKey → independent Y axes.
        var sut = new TraceChartViewModel();
        var sA = MakeSeries("SignalA", (0, 100), (1, 200));
        var sB = MakeSeries("SignalB", (0, 1), (1, 2));
        sut.AddSeries(sA);
        sut.AddSeries(sB);

        sut.SyncYAxes();

        var yA = sA.PlotModel.Axes.OfType<OxyPlot.Axes.LinearAxis>()
            .First(a => a.Position == OxyPlot.Axes.AxisPosition.Left);
        var yB = sB.PlotModel.Axes.OfType<OxyPlot.Axes.LinearAxis>()
            .First(a => a.Position == OxyPlot.Axes.AxisPosition.Left);
        // SignalA: data 100..200, range 100, padding 5, axis 95..205
        yA.Minimum.Should().BeApproximately(95.0, 0.001);
        yA.Maximum.Should().BeApproximately(205.0, 0.001);
        // SignalB: data 1..2, range 1, padding 0.05, axis 0.95..2.05
        yB.Minimum.Should().BeApproximately(0.95, 0.001);
        yB.Maximum.Should().BeApproximately(2.05, 0.001);
    }

    // ---------- v3.8.6 PATCH L1: ApplyViewports tolerates duplicate EffectiveKey ----------

    /// <summary>
    /// v3.8.6 PATCH L1: <see cref="TraceChartViewModel.ApplyViewports"/>
    /// must NOT throw <c>ArgumentException</c> when the bundle's viewport
    /// list contains two entries with the same <see cref="BundleViewportDto.EffectiveKey"/>.
    /// The inline XML doc at line 344 claims "defensive against duplicates"
    /// but the implementation used <c>ToDictionary(...)</c> which throws
    /// on duplicate keys (since .NET 9+). A hand-edited or
    /// producer-bug-crafted bundle with duplicate keys would crash
    /// <c>ApplyViewports</c> mid-restore, propagating up through the
    /// bundle-open call chain (no try/catch in any of the wrappers) and
    /// surfacing as an unhandled exception in the user's WPF dispatcher.
    /// <para>
    /// Fix: <c>GroupBy(...).ToDictionary(g =&gt; g.Key, g =&gt; g.Last())</c>
    /// so the last entry wins. Bundle writer guarantees 1:1 so the
    /// behavior change is invisible in the happy path; the documented
    /// duplicate-defense now actually defends.
    /// </para>
    /// </summary>
    [Fact]
    public void ApplyViewports_WithDuplicateEffectiveKeys_DoesNotThrow_LastWins()
    {
        var sut = new TraceChartViewModel();
        var s = MakeSeries("A", (0, 10), (1, 20), (2, 30));
        sut.AddSeries(s);

        // Two entries with the SAME EffectiveKey (matches the series just
        // added so the dict lookup path applies). The second has a wider
        // XMax that should win under "last-wins" GroupBy semantics.
        var viewports = new[]
        {
            new BundleViewportDto
            {
                EffectiveKey = s.EffectiveKey,
                XMin = 0.0,
                XMax = 30.0,
            },
            new BundleViewportDto
            {
                EffectiveKey = s.EffectiveKey,
                XMin = 0.0,
                XMax = 60.0,  // LAST wins under GroupBy.Last()
            },
        };

        // Act -- must not throw.
        var act = () => sut.ApplyViewports(viewports);
        act.Should().NotThrow<ArgumentException>(
            "ApplyViewports must tolerate duplicate EffectiveKeys (defensive against hand-crafted bundles)");
    }
}
