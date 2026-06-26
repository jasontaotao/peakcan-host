using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.Tests.Collections;
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
/// <para>
/// <b>v1.2.1 PATCH (Task 5):</b> the ctor calls
/// <see cref="LeakedApplicationReset.CleanupLeakedApplication"/> to null
/// out any leaked <see cref="System.Windows.Application.Current"/> from a
/// sibling test class (xUnit runs test classes in parallel). Without
/// this, the inline path inside <see cref="ApplyFrame"/> would route
/// through <c>Dispatcher.InvokeAsync</c> on a dead dispatcher and
/// <see cref="SignalViewModel.Latest"/> would stay empty.
/// </para>
/// </summary>
public class SignalViewModelTests
{
    // v1.2.1 PATCH Task 5: defensive cleanup of leaked Application.Current
    // before each test (ctor runs once per test instance in xUnit).
    public SignalViewModelTests() => LeakedApplicationReset.CleanupLeakedApplication();

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

        vm.ApplyFrame(frame, msg); DrainPending_ForTest(vm);

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

        vm.ApplyFrame(frame, msg); DrainPending_ForTest(vm);

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

        vm.ApplyFrame(frame1, msg); DrainPending_ForTest(vm);
        vm.ApplyFrame(frame2, msg); DrainPending_ForTest(vm);

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

        vm.ApplyFrame(frame, msg); DrainPending_ForTest(vm);

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

        vm.ApplyFrame(frame, msg); DrainPending_ForTest(vm);
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

        var act = () => vm.ApplyFrame(frame, msg); DrainPending_ForTest(vm);

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

        vm.ApplyFrame(frame, msg); DrainPending_ForTest(vm);

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

        vm.ApplyFrame(frame1, msg); DrainPending_ForTest(vm);
        vm.Latest[0].IsSelected = true;  // user checks the checkbox

        vm.ApplyFrame(frame2, msg); DrainPending_ForTest(vm);

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
        vm.ApplyFrame(MakeFrame(0x100, 0x01), msg); DrainPending_ForTest(vm);

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
        vm.ApplyFrame(MakeFrame(0x100, 0x01), msg); DrainPending_ForTest(vm);
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
        vm.ApplyFrame(MakeFrame(0x100, 0x01), msg); DrainPending_ForTest(vm);

        var act = () => vm.PlotAllCommand.Execute(null);
        act.Should().NotThrow();
    }

    // --- v1.2.3 PATCH-2: ApplyFrame dispatcher removal ---

    [Fact]
    public void ApplyFrame_Does_Not_Mutate_Latest_Immediately_When_Running_Off_UI_Thread()
    {
        // v1.2.3 PATCH-2: the v1.2.3 first cut only throttled ApplyFilter
        // (the Clear+Add pass) but left the per-frame RunOnUiPost in
        // place. At 8 kfps that still queued 8 000 dispatcher
        // operations per second; the throttle's escape condition
        // (FilteredSignals.Count == Latest.Count) also failed in
        // practice because every Upsert grew Latest by 1 before
        // ApplyFilter ran, so the rebuild still happened. The PATCH-2
        // fix removes the RunOnUiPost entirely: ApplyFrame now only
        // buffers; the DispatcherTimer tick at ~30 Hz drains the
        // buffer onto the UI thread in batches. The test pins the
        // off-thread "buffer only, no immediate apply" contract.
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"), Sig("Rpm"));
        var frame = MakeFrame(0x100, 0x01);

        // ApplyFrame is documented as callable from any thread; the
        // 8 kfps decode path is the SDK read loop / DbcDecodeBackground
        // worker. Pre-PATCH-2 it did RunOnUiPost; the post-state of
        // Latest is only visible AFTER the dispatch hop. PATCH-2
        // removes the post, so Latest is still empty immediately
        // after a synchronous call.
        vm.ApplyFrame(frame, msg);

        vm.Latest.Should().BeEmpty(
            "ApplyFrame now buffers only; the DispatcherTimer tick drains onto the UI thread");
    }

    [Fact]
    public void DrainPending_Applies_Buffered_Frames_To_Latest()
    {
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"), Sig("Rpm"));
        var frame = MakeFrame(0x100, 0x01);

        // Buffer 100 frames; the test entry DrainPending_ForTest
        // flushes them onto Latest in one batch. The expected post-
        // state: 1 row per (Message, Signal) pair (Upsert semantics)
        // — 2 rows total, not 100.
        for (var i = 0; i < 100; i++) vm.ApplyFrame(frame, msg); DrainPending_ForTest(vm);

        DrainPending_ForTest(vm);

        vm.Latest.Should().HaveCount(2,
            "Upsert key is (Message, Signal); 100 frames to the same message still yields 2 rows");
        vm.Latest.Select(e => e.Signal).Should().BeEquivalentTo(SpeedAndRpm);
    }

    private static readonly string[] SpeedAndRpm = new[] { "Speed", "Rpm" };

    [Fact]
    public void DrainPending_Triggers_Filter_After_Buffer_Flush()
    {
        // v1.2.3 PATCH-2: the v1.2.3 first cut left ApplyFilter inside
        // ApplyEntries, so it ran on the (now-removed) dispatcher
        // post. PATCH-2 moves ApplyFilter into the same DispatcherTimer
        // tick that drains the buffer, so the filter is always
        // up-to-date with whatever just landed in Latest.
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"), Sig("Rpm"));
        vm.SearchText = "Rpm";

        vm.ApplyFrame(MakeFrame(0x100, 0x01), msg); DrainPending_ForTest(vm);
        DrainPending_ForTest(vm);

        vm.FilteredSignals.Should().ContainSingle(e => e.Signal == "Rpm",
            "after drain FilteredSignals reflects Latest under the active SearchText");
        vm.FilteredSignals.Should().HaveCount(1);
    }

    [Fact]
    public void ApplyFrame_Coalesces_Same_Signal_Updates_Into_Single_Upsert()
    {
        // The decoder runs on every frame but the per-signal value
        // usually only changes occasionally. PATCH-2's drain batches
        // all buffered values for a (Message, Signal) into a single
        // Latest[i] = entry assignment, regardless of how many
        // frames in between carried the same key. This keeps the
        // CollectionChanged rate ≤ 30 Hz × unique-keys rather than
        // 8 kHz × unique-keys.
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"));
        var frame = MakeFrame(0x100, 0x10);

        // First buffer + drain establishes a row.
        vm.ApplyFrame(frame, msg); DrainPending_ForTest(vm);
        DrainPending_ForTest(vm);
        vm.Latest.Should().HaveCount(1);

        // Now buffer many updates to the same key.
        for (var i = 0; i < 50; i++) vm.ApplyFrame(MakeFrame(0x100, (byte)i), msg);
        DrainPending_ForTest(vm);

        vm.Latest.Should().HaveCount(1,
            "100 buffered frames + 50 more = still 1 row; the drain coalesces by (Message, Signal)");
    }

    [Fact]
    public void ApplyFrame_Chart_Samples_Still_Forward_To_ChartVm()
    {
        // The chart VM relies on every decoded signal reaching it; the
        // v1.2.3 PATCH-2 batching must not drop chart samples. The
        // contract is: the user adds a signal to the chart
        // (<c>AddSignal</c>), then the drain tick appends one sample
        // per frame; the LineSeries.Points count grows by one per
        // drain (matching the pre-PATCH-2 contract — see
        // SignalChartViewModelTests.AppendSample_*_Drains_To_Points).
        var chartVm = new SignalChartViewModel();
        chartVm.AddSignal("M1.Speed", "Speed");
        var vm = new SignalViewModel(chartVm);
        var msg = Msg(0x100, "M1", Sig("Speed"));
        var frame = MakeFrame(0x100, 0x42);

        vm.ApplyFrame(frame, msg); DrainPending_ForTest(vm);
        chartVm.DrainBufferForTest();

        var series = (OxyPlot.Series.LineSeries)chartVm.PlotModel.Series[0];
        series.Points.Should().HaveCount(1,
            "the PATCH-2 drain tick must hand the sample to the chart VM exactly as the pre-PATCH-2 RunOnUiPost did");
    }

    // --- v1.2.3 test helpers ---

    /// <summary>
    /// Trigger the PATCH-2 drain tick from a unit test (no
    /// <c>DispatcherTimer</c> in xUnit's MTA context). The production
    /// path is a 33 ms <c>DispatcherTimer</c> started in the VM ctor
    /// when an <c>Application</c> is available; the test path exposes
    /// a single internal <c>DrainPendingForTest</c> method that does
    /// the same work synchronously.
    /// </summary>
    private static void DrainPending_ForTest(SignalViewModel vm) =>
        vm.GetType()
          .GetMethod("DrainPendingForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
          .Invoke(vm, null);

    /// <summary>
    /// PATCH-2 convenience helper: buffer a frame and drain the
    /// pending work immediately so the test sees the post-state
    /// synchronously. Replaces the pre-PATCH-2 "ApplyFrame mutates
    /// <c>Latest</c> inline" contract that the v1.2.2-era tests
    /// were written against.
    /// </summary>
    private static void ApplyFrame_ForTest(SignalViewModel vm, CanFrame frame, Message msg)
    {
        vm.ApplyFrame(frame, msg); DrainPending_ForTest(vm);
        DrainPending_ForTest(vm);
    }

    // --- v1.2.3 dispatcher-starvation hardening (P1) ---

    [Fact]
    public void ApplyFilter_Empty_SearchText_Rebuilds_FilteredSignals_To_Match_Latest()
    {
        // v1.2.3: ApplyFilter must produce a FilteredSignals view that
        // mirrors Latest when SearchText is empty. Pre-1.2.3 the
        // contract held because ApplyFilter always rebuilt; the v1.2.3
        // change adds throttling, so this test pins the "first rebuild
        // is exhaustive" invariant that the throttling code must
        // preserve.
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"), Sig("Rpm"), Sig("Temp"));
        vm.ApplyFrame(MakeFrame(0x100, 0x01), msg); DrainPending_ForTest(vm);

        vm.FilteredSignals.Should().HaveCount(3,
            "empty SearchText means the filter is a pass-through");
        vm.Latest.Should().HaveCount(3);
    }

    [Fact]
    public void ApplyFilter_SearchText_Changed_Triggers_Rebuild()
    {
        // v1.2.3: changing SearchText must immediately rebuild
        // FilteredSignals (the throttle is only for "no-op" rebuilds
        // when the pattern is unchanged and the window has not elapsed).
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"), Sig("Rpm"), Sig("Temp"));
        vm.ApplyFrame(MakeFrame(0x100, 0x01), msg); DrainPending_ForTest(vm);
        FilteredSignalsRebuildCount(vm).Should().Be(1, "first ApplyFilter rebuilds");

        vm.SearchText = "Rpm";
        ApplyFilter_ForTest(vm);

        vm.FilteredSignals.Should().ContainSingle(e => e.Signal == "Rpm",
            "filter must drop Speed/Temp and keep the matching Rpm row");
        vm.FilteredSignals.Should().HaveCount(1);
    }

    [Fact]
    public void ApplyFilter_Throttles_When_Pattern_Unchanged_And_Window_Not_Elapsed()
    {
        // v1.2.3 P1: at 8 kfps the pre-1.2.3 ApplyFilter rebuilt
        // FilteredSignals on every frame, generating up to 8 000
        // CollectionChanged events per second and saturating the WPF
        // dispatcher. Throttling: do not rebuild if the SearchText has
        // not changed AND the throttle window has not elapsed.
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"), Sig("Rpm"));
        vm.ApplyFrame(MakeFrame(0x100, 0x01), msg); DrainPending_ForTest(vm);
        var before = FilteredSignalsRebuildCount(vm);

        // Second ApplyFilter call within the throttle window must not
        // rebuild. The exact throttle interval is private; what we
        // pin is that two back-to-back calls do not both rebuild.
        ApplyFilter_ForTest(vm);

        FilteredSignalsRebuildCount(vm).Should().Be(before,
            "back-to-back ApplyFilter with unchanged SearchText must be coalesced");
    }

    [Fact]
    public void ApplyFilter_Rebuilds_After_SearchText_Change()
    {
        // v1.2.3 P1: the throttle is a window, not a one-shot. When
        // SearchText changes, the next ApplyFilter must rebuild so
        // the view reflects the new pattern. We exercise that
        // escape hatch explicitly because wall-clock-based testing
        // of the 100 ms window is flaky.
        var vm = new SignalViewModel();
        var msg = Msg(0x100, "M1", Sig("Speed"), Sig("Rpm"), Sig("Temp"));
        vm.ApplyFrame(MakeFrame(0x100, 0x01), msg); DrainPending_ForTest(vm);
        var before = FilteredSignalsRebuildCount(vm);

        vm.SearchText = "Sp";   // matches Speed only
        ApplyFilter_ForTest(vm);

        var after = FilteredSignalsRebuildCount(vm);
        after.Should().BeGreaterThan(before,
            "SearchText change must trigger exactly one additional rebuild");
        vm.FilteredSignals.Should().ContainSingle(e => e.Signal == "Speed");
    }

    [Fact]
    public void ApplyFilter_Throttled_Rebuild_Stays_Consistent_With_Latest()
    {
        // v1.2.3 P1: when a rebuild actually happens (e.g. after a
        // SearchText change or window elapse), FilteredSignals must
        // match Latest under the active filter — no stale rows, no
        // missing rows. This pins the contract that the throttle
        // does not skip a required rebuild.
        var vm = new SignalViewModel();
        var msg1 = Msg(0x100, "M1", Sig("Speed"), Sig("Rpm"));
        vm.ApplyFrame(MakeFrame(0x100, 0x01), msg1); DrainPending_ForTest(vm);
        vm.SearchText = "Rpm";
        ApplyFilter_ForTest(vm);

        var msg2 = Msg(0x200, "M2", Sig("RpmMap"), Sig("Temp"));
        vm.ApplyFrame(MakeFrame(0x200, 0x01), msg2); DrainPending_ForTest(vm);
        ApplyFilter_ForTest(vm);   // still throttled window after first rebuild

        // Force a real rebuild via the documented SearchText-change
        // escape hatch (change to a different non-empty pattern that
        // still matches Rpm-prefixed rows).
        vm.SearchText = "RpmM";
        ApplyFilter_ForTest(vm);

        vm.FilteredSignals.Should().ContainSingle(e => e.Signal == "RpmMap",
            "after a real rebuild FilteredSignals reflects Latest under the new pattern");
        vm.FilteredSignals.Select(e => e.Signal).Should().NotContain("Speed");
    }

    // --- v1.2.3 test helpers ---

    /// <summary>
    /// Expose the throttling logic. <c>OnSearchTextChanged</c> already
    /// calls <c>ApplyFilter</c> for free, so a public trigger for tests
    /// is the simplest way to drive a second/third filter pass without
    /// having to synthesise an 8 kfps frame stream.
    /// </summary>
    private static void ApplyFilter_ForTest(SignalViewModel vm) =>
        vm.GetType()
          .GetMethod("OnSearchTextChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
          .Invoke(vm, new object[] { vm.SearchText });

    /// <summary>
    /// Read the test-visible FilteredSignals rebuild counter via
    /// reflection. <c>ObservableCollection.Clear</c> is the only
    /// observable side-effect of <c>ApplyFilter</c>; the counter is
    /// the canonical hook for asserting throttling semantics in
    /// unit tests without depending on wall-clock timing.
    /// </summary>
    private static int FilteredSignalsRebuildCount(SignalViewModel vm) =>
        (int)(vm.GetType()
                 .GetProperty("FilterRebuildCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                 .GetValue(vm) ?? 0);
}
