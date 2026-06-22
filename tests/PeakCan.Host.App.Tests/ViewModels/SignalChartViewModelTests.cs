using FluentAssertions;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// <see cref="SignalChartViewModel"/> owns the real-time OxyPlot chart
/// for the Signal tab. Tests exercise AddSignal / RemoveSignal /
/// AppendSample / DrainBufferForTest / Reset — all run inline (no
/// <c>Application</c>, no <c>DispatcherTimer</c>).
/// </summary>
public class SignalChartViewModelTests
{
    [Fact]
    public void Default_PlotModel_Has_TimeAxis_And_ValueAxis()
    {
        var vm = new SignalChartViewModel();

        vm.PlotModel.Should().NotBeNull();
        vm.PlotModel.Axes.Should().HaveCount(2);
        vm.PlotModel.Axes[0].Should().BeOfType<LinearAxis>()
            .Which.Position.Should().Be(AxisPosition.Bottom);
        vm.PlotModel.Axes[1].Should().BeOfType<LinearAxis>()
            .Which.Position.Should().Be(AxisPosition.Left);
    }

    [Fact]
    public void Default_HasSignals_Is_False()
    {
        var vm = new SignalChartViewModel();
        vm.HasSignals.Should().BeFalse();
        vm.SignalCount.Should().Be(0);
    }

    [Fact]
    public void AddSignal_Creates_LineSeries_With_Palette_Color()
    {
        var vm = new SignalChartViewModel();

        vm.AddSignal("M1.Speed", "Speed");

        vm.PlotModel.Series.Should().HaveCount(1);
        vm.PlotModel.Series[0].Should().BeOfType<LineSeries>();
        ((LineSeries)vm.PlotModel.Series[0]).Title.Should().Be("Speed");
    }

    [Fact]
    public void AddSignal_Series_Added_To_PlotModel()
    {
        var vm = new SignalChartViewModel();

        vm.AddSignal("M1.Speed", "Speed");
        vm.AddSignal("M1.Rpm", "Rpm");

        vm.PlotModel.Series.Should().HaveCount(2);
        vm.HasSignals.Should().BeTrue();
        vm.SignalCount.Should().Be(2);
    }

    [Fact]
    public void AddSignal_Duplicate_Key_Is_NoOp()
    {
        var vm = new SignalChartViewModel();

        vm.AddSignal("M1.Speed", "Speed");
        vm.AddSignal("M1.Speed", "Speed");

        vm.PlotModel.Series.Should().HaveCount(1);
        vm.SignalCount.Should().Be(1);
    }

    [Fact]
    public void AddSignal_Distinct_Keys_Get_Different_Colors()
    {
        var vm = new SignalChartViewModel();

        vm.AddSignal("M1.Speed", "Speed");
        vm.AddSignal("M1.Rpm", "Rpm");

        var c0 = ((LineSeries)vm.PlotModel.Series[0]).Color;
        var c1 = ((LineSeries)vm.PlotModel.Series[1]).Color;
        c0.Should().NotBe(c1);
    }

    [Fact]
    public void RemoveSignal_Removes_LineSeries_From_PlotModel()
    {
        var vm = new SignalChartViewModel();
        vm.AddSignal("M1.Speed", "Speed");
        vm.AddSignal("M1.Rpm", "Rpm");

        vm.RemoveSignal("M1.Speed");

        vm.PlotModel.Series.Should().HaveCount(1);
        vm.SignalCount.Should().Be(1);
        ((LineSeries)vm.PlotModel.Series[0]).Title.Should().Be("Rpm");
    }

    [Fact]
    public void RemoveSignal_Unknown_Key_Does_Not_Throw()
    {
        var vm = new SignalChartViewModel();

        var act = () => vm.RemoveSignal("NoSuch.Signal");

        act.Should().NotThrow();
        vm.PlotModel.Series.Should().BeEmpty();
    }

    [Fact]
    public void RemoveSignal_Last_Signal_Resets_HasSignals()
    {
        var vm = new SignalChartViewModel();
        vm.AddSignal("M1.Speed", "Speed");

        vm.RemoveSignal("M1.Speed");

        vm.HasSignals.Should().BeFalse();
        vm.SignalCount.Should().Be(0);
    }

    [Fact]
    public void AppendSample_For_Selected_Signal_Buffers_Point()
    {
        var vm = new SignalChartViewModel();
        vm.AddSignal("M1.Speed", "Speed");

        vm.AppendSample("M1.Speed", 42.0, 1_000_000UL);
        vm.DrainBufferForTest();

        var series = (LineSeries)vm.PlotModel.Series[0];
        series.Points.Should().HaveCount(1);
        series.Points[0].Y.Should().Be(42.0);
    }

    [Fact]
    public void AppendSample_For_Unselected_Signal_Is_NoOp()
    {
        var vm = new SignalChartViewModel();
        vm.AddSignal("M1.Speed", "Speed");

        vm.AppendSample("M1.Rpm", 3000.0, 1_000_000UL);
        vm.DrainBufferForTest();

        var series = (LineSeries)vm.PlotModel.Series[0];
        series.Points.Should().BeEmpty("Rpm is not charted");
    }

    [Fact]
    public void AppendSample_Multiple_Signals_Independent_Buffers()
    {
        var vm = new SignalChartViewModel();
        vm.AddSignal("M1.Speed", "Speed");
        vm.AddSignal("M1.Rpm", "Rpm");

        vm.AppendSample("M1.Speed", 100.0, 1_000_000UL);
        vm.AppendSample("M1.Rpm", 3000.0, 1_000_000UL);
        vm.DrainBufferForTest();

        var speed = (LineSeries)vm.PlotModel.Series[0];
        var rpm = (LineSeries)vm.PlotModel.Series[1];
        speed.Points.Should().HaveCount(1);
        rpm.Points.Should().HaveCount(1);
        speed.Points[0].Y.Should().Be(100.0);
        rpm.Points[0].Y.Should().Be(3000.0);
    }

    [Fact]
    public void AppendSample_Relative_Time_Uses_First_Sample_As_T0()
    {
        var vm = new SignalChartViewModel();
        vm.AddSignal("M1.Speed", "Speed");

        vm.AppendSample("M1.Speed", 10.0, 1_000_000UL);   // t0 = 1s
        vm.AppendSample("M1.Speed", 20.0, 2_000_000UL);   // t = 2s → relative 1s
        vm.DrainBufferForTest();

        var series = (LineSeries)vm.PlotModel.Series[0];
        // Coalescing: only the latest value survives between ticks.
        series.Points.Should().HaveCount(1);
        series.Points[0].X.Should().Be(1.0, "relative to t0=1s");
        series.Points[0].Y.Should().Be(20.0, "latest value wins");
    }

    [Fact]
    public void DrainBuffer_Clears_Buffer()
    {
        var vm = new SignalChartViewModel();
        vm.AddSignal("M1.Speed", "Speed");

        vm.AppendSample("M1.Speed", 10.0, 1_000_000UL);
        vm.DrainBufferForTest();

        // Second drain with no new samples should not add points.
        vm.DrainBufferForTest();

        var series = (LineSeries)vm.PlotModel.Series[0];
        series.Points.Should().HaveCount(1);
    }

    [Fact]
    public void DrainBuffer_Multiple_Ticks_Accumulate_Points()
    {
        var vm = new SignalChartViewModel();
        vm.AddSignal("M1.Speed", "Speed");

        vm.AppendSample("M1.Speed", 10.0, 1_000_000UL);
        vm.DrainBufferForTest();

        vm.AppendSample("M1.Speed", 20.0, 2_000_000UL);
        vm.DrainBufferForTest();

        var series = (LineSeries)vm.PlotModel.Series[0];
        series.Points.Should().HaveCount(2);
        series.Points[0].Y.Should().Be(10.0);
        series.Points[1].Y.Should().Be(20.0);
    }

    [Fact]
    public void DrainBuffer_Trims_Series_To_MaxPoints()
    {
        var vm = new SignalChartViewModel();
        vm.AddSignal("M1.Speed", "Speed");

        for (ulong i = 0; i < (ulong)(SignalChartViewModel.MaxPointsPerSeries + 1); i++)
        {
            vm.AppendSample("M1.Speed", i, i * 1000UL);
            vm.DrainBufferForTest();
        }

        var series = (LineSeries)vm.PlotModel.Series[0];
        series.Points.Should().HaveCount(SignalChartViewModel.MaxPointsPerSeries);
        // Oldest point removed; newest retained.
        series.Points[^1].Y.Should().Be(SignalChartViewModel.MaxPointsPerSeries);
    }

    [Fact]
    public void Reset_Clears_All_Series_And_Buffers()
    {
        var vm = new SignalChartViewModel();
        vm.AddSignal("M1.Speed", "Speed");
        vm.AddSignal("M1.Rpm", "Rpm");
        vm.AppendSample("M1.Speed", 10.0, 1_000_000UL);
        vm.AppendSample("M1.Rpm", 3000.0, 1_000_000UL);

        vm.Reset();

        vm.PlotModel.Series.Should().BeEmpty();
        vm.HasSignals.Should().BeFalse();
        vm.SignalCount.Should().Be(0);
    }

    [Fact]
    public void Reset_Allows_ReAdding_Signal()
    {
        var vm = new SignalChartViewModel();
        vm.AddSignal("M1.Speed", "Speed");
        vm.AppendSample("M1.Speed", 10.0, 1_000_000UL);
        vm.DrainBufferForTest();

        vm.Reset();
        vm.AddSignal("M1.Speed", "Speed");
        vm.AppendSample("M1.Speed", 99.0, 5_000_000UL);
        vm.DrainBufferForTest();

        var series = (LineSeries)vm.PlotModel.Series[0];
        series.Points.Should().HaveCount(1);
        series.Points[0].Y.Should().Be(99.0);
    }

    [Fact]
    public void RemoveSignal_Then_Append_Is_NoOp()
    {
        var vm = new SignalChartViewModel();
        vm.AddSignal("M1.Speed", "Speed");
        vm.RemoveSignal("M1.Speed");

        vm.AppendSample("M1.Speed", 10.0, 1_000_000UL);
        vm.DrainBufferForTest();

        vm.PlotModel.Series.Should().BeEmpty();
    }
}
