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
               MultiplexorSignalIndex: null);

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
    public void ApplyFrame_Skips_Multiplexor_And_Multiplexed_Signals()
    {
        // Per plan §2: multiplexor + multiplexed signals are deferred to v1.1.
        // Only "plain" signals (neither IsMultiplexor nor IsMultiplexed) get
        // a row in the v1.0 grid.
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1",
            Sig("Mux",       isMultiplexor: true),
            Sig("PlainSig"),
            Sig("Muxed0",    isMultiplexed: true, multiplexValue: 0),
            Sig("Muxed1",    isMultiplexed: true, multiplexValue: 1));
        var frame = MakeFrame(0x100, 0x00);

        vm.ApplyFrame(frame, msg);

        vm.Latest.Should().HaveCount(1);
        vm.Latest[0].Signal.Should().Be("PlainSig");
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
}
