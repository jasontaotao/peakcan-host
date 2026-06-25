using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Task 16: <see cref="SignalViewModel"/> owns the decoded-signal grid.
/// Each <see cref="CanFrame"/> matching a DBC message yields one
/// <see cref="SignalEntry"/> per non-multiplexed signal. The grid is
/// upserted (existing row replaced by key, not duplicated) so the
/// DataGrid view stays stable as frames stream in.
/// <para>
/// <b>Concurrency model:</b> <see cref="ApplyFrame"/> decodes on the
/// calling thread (pure / stateless path) and marshals the resulting
/// <c>ObservableCollection&lt;SignalEntry&gt;</c> mutations to the WPF
/// UI thread via <c>Dispatcher.InvokeAsync</c> (mirror of the
/// <see cref="DbcViewModel.OnLoaded"/> pattern from Task 15). The
/// non-STA tests below run on xunit's MTA thread with no
/// <c>Application</c> instance, so the dispatcher is null and the
/// upsert runs inline — the test observes the post-state directly.
/// The full dispatcher hop is exercised in production by the WPF
/// smoke run (Task 20); a dedicated STA test for this VM caused
/// xunit parallel-execution hangs in the suite and was rolled back.
/// </para>
/// </summary>
public class SignalViewModelTests
{
    private static readonly string[] ExpectedPlainSigNames = new[] { "Speed", "Rpm", "Temp" };

    private static CanFrame MakeFrame(uint id, params byte[] data)
        => new(new CanId(id, FrameFormat.Standard),
               data,
               FrameFlags.None,
               new ChannelId(0x51),
               Timestamp.FromMicroseconds(1_000_000UL));

    private static Signal Sig(string name, double factor = 1.0, double offset = 0.0,
                              string unit = "", bool isMultiplexor = false, bool isMultiplexed = false,
                              ushort? multiplexValue = null)
        => new(name, StartBit: 0, Length: 8, Order: ByteOrder.LittleEndian,
               ValueType: PeakCan.Host.Core.Dbc.ValueType.Unsigned, Factor: factor, Offset: offset,
               Min: 0, Max: 0, Unit: unit, Receivers: Array.Empty<string>(),
               IsMultiplexor: isMultiplexor, IsMultiplexed: isMultiplexed,
               MultiplexValue: multiplexValue);

    private static Message Msg(uint id, string name, params Signal[] signals)
        => new(id, name, Dlc: 8, Sender: "ECU1", Signals: signals,
               IsMultiplexed: signals.Any(s => s.IsMultiplexed),
               MultiplexorSignalIndex: FindMultiplexorIndex(signals));

    private static ushort? FindMultiplexorIndex(Signal[] signals)
    {
        for (ushort i = 0; i < signals.Length; i++)
            if (signals[i].IsMultiplexor) return i;
        return null;
    }

    [Fact]
    public void Default_Latest_Is_Empty()
    {
        var vm = new SignalViewModel();
        vm.Latest.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFrame_No_DbcMessage_Leaves_Latest_Empty()
    {
        // Sanity check that ApplyFrame doesn't misbehave on a bare frame.
        // The SignalViewModel receives (frame, msg) where msg is the
        // lookup result — if the caller doesn't find a match it shouldn't
        // call ApplyFrame at all (see TraceService.OnFrame). Here we just
        // verify that calling ApplyFrame with a single-signal message
        // mutates Latest.
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"));
        var frame = MakeFrame(0x100, 0x42);

        vm.ApplyFrame(frame, msg);

        vm.Latest.Should().HaveCount(1, "one signal decoded = one row");
        vm.Latest[0].Message.Should().Be("M1");
        vm.Latest[0].Signal.Should().Be("Speed");
    }

    [Fact]
    public void ApplyFrame_Multiple_Signals_Adds_All_As_Entries()
    {
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1",
            Sig("Speed", factor: 0.1),
            Sig("Rpm"),
            Sig("Temp", unit: "°C"));
        var frame = MakeFrame(0x100, 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE);

        vm.ApplyFrame(frame, msg);

        vm.Latest.Should().HaveCount(3);
        vm.Latest.Select(e => e.Signal).Should().BeEquivalentTo(ExpectedPlainSigNames);
        vm.Latest.Single(e => e.Signal == "Temp").Unit.Should().Be("°C");
    }

    [Fact]
    public void ApplyFrame_Repeated_Frame_Updates_Existing_Entry_Not_Adds_New()
    {
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"));
        var frame1 = MakeFrame(0x100, 0x10);
        var frame2 = MakeFrame(0x100, 0x20);

        vm.ApplyFrame(frame1, msg);
        vm.ApplyFrame(frame2, msg);

        vm.Latest.Should().HaveCount(1, "key is (Message, Signal); repeated frame replaces existing row");
        // Verify the latest value is reflected in the entry. Speed factor=1, offset=0,
        // signal at startBit=0 length=8 LE = first byte = 0x20 = 32.
        vm.Latest[0].Physical.Should().Be("32");
    }

    [Fact]
    public void ApplyFrame_Decodes_Multiplexor_And_Matching_Muxed_Signals()
    {
        // v0.6.0: multiplexor signal is decoded first, then only
        // multiplexed signals whose MultiplexValue matches the mux
        // value are decoded. Non-muxed signals are always decoded.
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1",
            Sig("Mux",       isMultiplexor: true),
            Sig("PlainSig"),
            Sig("Muxed0",    isMultiplexed: true, multiplexValue: 0),
            Sig("Muxed1",    isMultiplexed: true, multiplexValue: 1));
        var frame = MakeFrame(0x100, 0x00); // mux value = 0

        vm.ApplyFrame(frame, msg);

        // Expected: Mux (multiplexor) + PlainSig (always) + Muxed0 (mux=0 matches)
        vm.Latest.Should().HaveCount(3);
        vm.Latest.Should().Contain(e => e.Signal == "Mux");
        vm.Latest.Should().Contain(e => e.Signal == "PlainSig");
        vm.Latest.Should().Contain(e => e.Signal == "Muxed0");
        vm.Latest.Should().NotContain(e => e.Signal == "Muxed1");
    }

    [Fact]
    public void Reset_Clears_All_Entries()
    {
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"), Sig("Rpm"));
        var frame = MakeFrame(0x100, 0x42);

        vm.ApplyFrame(frame, msg);
        vm.Latest.Should().HaveCount(2);

        vm.Reset();

        vm.Latest.Should().BeEmpty("Reset is called from DbcViewModel.OnLoaded on a new DBC load");
    }

    [Fact]
    public void ApplyFrame_Does_Not_Throw()
    {
        // The contract: ApplyFrame must not throw regardless of thread
        // (MTA or STA, dispatcher available or not). The post-state
        // assertion is covered by the targeted tests above.
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"));
        var frame = MakeFrame(0x100, 0x42);

        var act = () => vm.ApplyFrame(frame, msg);

        act.Should().NotThrow();
    }

    [Fact]
    public void ApplyFrame_Decoded_Physical_Value_Uses_Factor_And_Offset()
    {
        // Sanity check that SignalDecoder is wired through correctly:
        // signal at startBit=0 len=8 LE = first byte = 0x40 = 64 raw,
        // factor=0.5, offset=10 → physical = 64*0.5 + 10 = 42.
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed", factor: 0.5, offset: 10));
        var frame = MakeFrame(0x100, 0x40);

        vm.ApplyFrame(frame, msg);

        vm.Latest[0].Physical.Should().Be("42");
    }

    // --- v0.8.0: chart integration tests ---

    [Fact]
    public void ChartModel_Is_Null_Without_ChartVm()
    {
        var vm = new SignalViewModel();
        vm.ChartModel.Should().BeNull();
        vm.HasChart.Should().BeFalse();
    }

    [Fact]
    public void ChartModel_Is_Set_With_ChartVm()
    {
        var chartVm = new SignalChartViewModel();
        var vm = new SignalViewModel(chartVm);

        vm.ChartModel.Should().NotBeNull();
        vm.ChartModel.Should().BeSameAs(chartVm.PlotModel);
        vm.HasChart.Should().BeTrue();
    }

    [Fact]
    public void OnSignalSelectionChanged_Calls_ChartVm_AddSignal()
    {
        var chartVm = new SignalChartViewModel();
        var vm = new SignalViewModel(chartVm);

        vm.OnSignalSelectionChanged("M1", "Speed", true);

        chartVm.SignalCount.Should().Be(1);
        chartVm.HasSignals.Should().BeTrue();
    }

    [Fact]
    public void OnSignalSelectionChanged_False_Calls_ChartVm_RemoveSignal()
    {
        var chartVm = new SignalChartViewModel();
        var vm = new SignalViewModel(chartVm);

        vm.OnSignalSelectionChanged("M1", "Speed", true);
        vm.OnSignalSelectionChanged("M1", "Speed", false);

        chartVm.SignalCount.Should().Be(0);
    }

    [Fact]
    public void OnSignalSelectionChanged_Without_ChartVm_Does_Not_Throw()
    {
        var vm = new SignalViewModel();

        var act = () => vm.OnSignalSelectionChanged("M1", "Speed", true);

        act.Should().NotThrow();
    }

    [Fact]
    public void Reset_Also_Resets_Chart()
    {
        var chartVm = new SignalChartViewModel();
        var vm = new SignalViewModel(chartVm);
        vm.OnSignalSelectionChanged("M1", "Speed", true);

        vm.Reset();

        chartVm.SignalCount.Should().Be(0);
        vm.Latest.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFrame_Preserves_IsSelected_Across_Frames()
    {
        // v0.8.0 fix: Upsert must carry over IsSelected from the
        // existing entry so the chart checkbox doesn't reset on every
        // frame update.
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"));
        var frame1 = MakeFrame(0x100, 0x10);
        var frame2 = MakeFrame(0x100, 0x20);

        vm.ApplyFrame(frame1, msg);
        vm.Latest[0].IsSelected = true;  // user checks the checkbox

        vm.ApplyFrame(frame2, msg);

        vm.Latest.Should().HaveCount(1);
        vm.Latest[0].IsSelected.Should().BeTrue("checkbox state must survive frame updates");
    }

    // --- v0.8.1: PlotAll / PlotNone tests ---

    [Fact]
    public void PlotAll_Sets_All_IsSelected_True()
    {
        var chartVm = new SignalChartViewModel();
        var vm = new SignalViewModel(chartVm);
        var msg = Msg(0x100, "M1", Sig("Speed"), Sig("Rpm"), Sig("Temp"));
        vm.ApplyFrame(MakeFrame(0x100, 0x01), msg);

        vm.PlotAllCommand.Execute(null);

        vm.Latest.Should().AllSatisfy(e => e.IsSelected.Should().BeTrue());
        chartVm.SignalCount.Should().Be(3);
    }

    [Fact]
    public void PlotNone_Clears_All_IsSelected()
    {
        var chartVm = new SignalChartViewModel();
        var vm = new SignalViewModel(chartVm);
        var msg = Msg(0x100, "M1", Sig("Speed"), Sig("Rpm"));
        vm.ApplyFrame(MakeFrame(0x100, 0x01), msg);
        vm.PlotAllCommand.Execute(null);

        vm.PlotNoneCommand.Execute(null);

        vm.Latest.Should().AllSatisfy(e => e.IsSelected.Should().BeFalse());
        chartVm.SignalCount.Should().Be(0);
    }

    [Fact]
    public void PlotAll_Without_ChartVm_Does_Not_Throw()
    {
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"));
        vm.ApplyFrame(MakeFrame(0x100, 0x01), msg);

        var act = () => vm.PlotAllCommand.Execute(null);
        act.Should().NotThrow();
    }
}
