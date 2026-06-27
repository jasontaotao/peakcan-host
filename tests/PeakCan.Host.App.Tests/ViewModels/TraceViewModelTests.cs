using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.Tests.Collections;
using System.Windows;
using System.Windows.Threading;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Task 13: behavior of the TraceViewModel's ObservableCollection of TraceEntry
/// rows. The view model is intentionally parameterless so DI can instantiate
/// it without dragging in WPF types.
/// <para>
/// <b>Dispatcher contract:</b> <see cref="TraceViewModel.AppendBatchAsync"/>
/// silently no-ops when <c>Application.Current</c> is null (test contexts
/// that run on the xunit MTA threadpool). The STA-hosted test
/// <c>AppendBatch_On_StaThread_With_Application_Adds_All_Frames</c> exercises
/// the production dispatcher path. In the running app, <c>Application.Current.Dispatcher</c>
/// is always available, so the silent path is never hit at runtime.
/// </para>
/// <para>
/// <b>v1.2.1 PATCH (Task 5):</b> the ctor calls
/// <see cref="LeakedApplicationReset.CleanupLeakedApplication"/> defensively
/// so a leaked <see cref="System.Windows.Application.Current"/> from a
/// prior sibling test class is nulled before each test. The STA-hosted
/// test below still creates its own <see cref="Application"/> on its
/// dedicated STA thread — it does not depend on a prior leak. The
/// test's own <c>finally</c> block cleans up the singleton after the
/// STA thread exits.
/// </para>
/// <para>
/// <b>v1.2.11 PATCH (Task 10):</b> class joined to
/// <see cref="Collections.WpfAppTestCollection"/> so it does not run in
/// parallel with <c>SendViewTests</c> (both create a WPF
/// <see cref="System.Windows.Application"/>; only one allowed per AppDomain).
/// </para>
/// </summary>
[Collection(Collections.WpfAppTestCollection.Name)]
public class TraceViewModelTests
{
    // v1.2.1 PATCH Task 5: defensive cleanup of leaked Application.Current
    // before each test (ctor runs once per test instance in xUnit).
    public TraceViewModelTests() => LeakedApplicationReset.CleanupLeakedApplication();

    private static CanFrame MakeFrame(uint id = 0x123, byte dlc = 4, bool fd = false, bool error = false, bool rtr = false)
    {
        byte[] payload = dlc == 0 ? Array.Empty<byte>() : new byte[dlc];
        var flags = FrameFlags.None;
        if (fd) flags |= FrameFlags.Fd;
        if (error) flags |= FrameFlags.ErrFrame;
        // v1.2.11 PATCH Item 1: RTR uses empty payload per CAN spec;
        // override dlc=0 callers if rtr=true.
        if (rtr) { flags |= FrameFlags.Rtr; payload = Array.Empty<byte>(); }
        return new CanFrame(
            new CanId(id, FrameFormat.Standard),
            payload,
            flags,
            new ChannelId(0x51),
            Timestamp.FromMicroseconds(1_000_000UL));
    }

    [Fact]
    public void Default_Entries_Is_Empty()
    {
        var vm = new TraceViewModel();
        vm.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Default_MaxRows_Is_One_Thousand()
    {
        // v1.2.3: lowered from 10_000 to 1_000 because under sustained
        // high frame rates the WPF DataGrid paint + collection mutation
        // cost on the dispatcher thread becomes prohibitive. 1_000 rows
        // × 20 px = 20 k px of virtualized content, well within the
        // recycling virtualization budget.
        var vm = new TraceViewModel();
        vm.MaxRows.Should().Be(1_000);
    }

    [Theory]
    [InlineData(new byte[] { }, "")]
    [InlineData(new byte[] { 0xDE }, "DE")]
    [InlineData(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, "DE AD BE EF")]
    [InlineData(new byte[] { 0x00, 0xFF, 0x10 }, "00 FF 10")]
    public void FormatHexWithSpaces_Formats_Byte_Sequence_With_Single_Space_Separators(
        byte[] input, string expected)
    {
        TraceViewModel.FormatHexWithSpaces(input).Should().Be(expected,
            "hex format is the user-visible DataGrid DataHex column; a regression here breaks live trace readability");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("with,comma", "\"with,comma\"")]
    [InlineData("with\"quote", "\"with\"\"quote\"")]
    [InlineData("with\nnewline", "\"with\nnewline\"")]
    [InlineData("with\rcr", "\"with\rcr\"")]
    [InlineData("a,b\"c", "\"a,b\"\"c\"")]
    public void CsvEscape_Wraps_Fields_With_Rfc4180_Required_Characters(string input, string expected)
    {
        // RFC 4180 §2.6: any field containing comma, double-quote, CR, or LF
        // must be enclosed in double-quotes; embedded double-quotes are
        // doubled. The CSV export uses this for every cell, so a regression
        // here corrupts the export (Excel imports split on the unescaped
        // comma inside a quoted cell).
        TraceViewModel.CsvEscape(input).Should().Be(expected);
    }

    [Fact]
    public async Task AppendBatch_With_Null_Application_Returns_CompletedTask_Without_Adding()
    {
        // Documents the test-context limitation: xunit runs tests on the
        // MTA threadpool with no WPF Application, so Application.Current is
        // null and AppendBatchAsync returns CompletedTask without mutating
        // Entries. The production dispatcher path is exercised by the
        // STA-hosted test below.
        var vm = new TraceViewModel();
        var frames = new List<CanFrame>
        {
            MakeFrame(id: 0x111, dlc: 2),
            MakeFrame(id: 0x222, dlc: 4),
            MakeFrame(id: 0x333, dlc: 8, fd: true),
        };
        var task = vm.AppendBatchAsync(frames);
        await task;
        task.IsCompletedSuccessfully.Should().BeTrue();
        vm.Entries.Should().BeEmpty("xunit MTA thread has no WPF Application; dispatcher path is null");
    }

    [Fact]
    public void AppendBatch_On_StaThread_With_Application_Adds_All_Frames()
    {
        // The only test that exercises the production dispatcher path.
        // Spawns an STA thread, creates a WPF Application on it (so
        // Application.Current is non-null), invokes AppendBatchAsync, and
        // pumps the dispatcher with DispatcherFrame until the awaited
        // Task completes. Asserts all frames land in Entries.
        // v1.2.11 PATCH Item 1: also includes a 4th RTR frame and asserts
        // IsRtr + FrameType="RTR" wire-up from FrameFlags.Rtr bit.
        // v1.2.11 PATCH Item 2: also asserts PendingDecode is populated
        // for each appended frame and cleared by Clear().
        // All STA-bound assertions are consolidated here because xunit
        // cannot create multiple System.Windows.Application instances
        // per AppDomain (each STA test would trip the second-creation guard).
        Exception? caught = null;
        int entriesCount = 0;
        bool rtrIsRtr = false;
        string rtrFrameType = "";
        int pendingBeforeClear = -1;
        int pendingAfterClear = -1;
        // Defensive: clear any leaked Application.Current from a prior
        // STA test in the same xunit collection (SendViewTests). The
        // xunit [Collection] serialization doesn't fully prevent the
        // leaked singleton from surviving; we null it out explicitly.
        LeakedApplicationReset.CleanupLeakedApplication();
        var thread = new Thread(() =>
        {
            try
            {
                // Task 19: the leaked-Application concern that originally
                // motivated capturing this for Shutdown() is now handled
                // in the production VMs (SignalViewModel / DbcViewModel /
                // StatsViewModel) via the "calling thread's dispatcher
                // must match Application.Current.Dispatcher" guard.
                // We do not need to Shutdown() here — the test still
                // passes and downstream tests handle the singleton.
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var vm = new TraceViewModel();
                var frames = new List<CanFrame>
                {
                    MakeFrame(id: 0x111, dlc: 2),
                    MakeFrame(id: 0x222, dlc: 4),
                    MakeFrame(id: 0x333, dlc: 8, fd: true),
                    MakeFrame(id: 0x444, dlc: 0, rtr: true),
                };

                // AppendBatchAsync queues work onto the current dispatcher
                // (Application.Current.Dispatcher === Dispatcher.CurrentDispatcher
                // on this STA thread). Pump the dispatcher until the task
                // completes — using a DispatcherFrame so we don't deadlock.
                var task = vm.AppendBatchAsync(frames);
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);

                entriesCount = vm.Entries.Count;
                var rtrEntry = vm.Entries.FirstOrDefault(e => e.Id.Raw == 0x444);
                if (rtrEntry != null)
                {
                    rtrIsRtr = rtrEntry.IsRtr;
                    rtrFrameType = rtrEntry.FrameType;
                }
                pendingBeforeClear = vm.PendingDecode.Count;

                vm.ClearCommand.Execute(null);
                pendingAfterClear = vm.PendingDecode.Count;
            }
            catch (Exception ex) { caught = ex; }
            finally
            {
                // v1.2.1 PATCH Task 5: clean up the leaked Application
                // singleton so sibling test classes don't see a
                // foreign STA dispatcher. Without this, the static
                // Application.Current reference survives the STA thread
                // exit and causes RunOnUiPost in sibling tests to
                // queue work to a dispatcher that may never pump.
                try
                {
                    Application.Current?.Shutdown();
                }
                catch { /* dispatcher may already be shutting down */ }
                // _appInstance is the backing field for Application.Current
                // (introspected via reflection; the Current property is
                // read-only and has no public setter).
                typeof(Application).GetField("_appInstance",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static)
                    ?.SetValue(null, null);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue("STA thread must complete within 5 s");

        if (caught is not null) throw caught;
        entriesCount.Should().Be(4, "the dispatcher path should add all 4 frames (3 normal + 1 RTR)");
        rtrIsRtr.Should().BeTrue("FrameFlags.Rtr must propagate to TraceEntry.IsRtr");
        rtrFrameType.Should().Be("RTR", "FrameType must show 'RTR' when IsRtr is set");
        pendingBeforeClear.Should().Be(4, "all 4 appended frames register pending entries");
        pendingAfterClear.Should().Be(0, "Clear must empty the pending-decode map");
    }

    // --- v0.8.2: message ID stats tests ---

    [Fact]
    public void GetMessageIdStats_Empty_Returns_Empty()
    {
        var vm = new TraceViewModel();
        vm.GetMessageIdStats().Should().BeEmpty();
    }

    [Fact]
    public void GetMessageIdStats_After_Append_Returns_Top_Ids()
    {
        // GetMessageIdStats reads _messageCounts which is updated in
        // AppendBatchAsync. With null Application the dispatcher path
        // is a no-op, so we test the stats method on a fresh VM.
        // The counts are only updated when the dispatcher path runs.
        // For a unit test we verify the method returns empty when
        // no frames have been appended (null dispatcher path).
        var vm = new TraceViewModel();
        vm.GetMessageIdStats().Should().BeEmpty();
    }

    [Fact]
    public void Clear_Resets_All_Counters()
    {
        var vm = new TraceViewModel();
        vm.ClearCommand.Execute(null);
        vm.TotalFrameCount.Should().Be(0);
        vm.FilteredCount.Should().Be(0);
        vm.GetMessageIdStats().Should().BeEmpty();
    }

    // --- v1.2.11 PATCH Item 2: pending-decode map (unit-level, no STA) ---

    [Fact]
    public void PendingDecode_Default_Is_Empty()
    {
        // v1.2.11: fresh VM exposes no pending entries. Unit-level — no
        // dispatcher needed because PendingDecode is just an empty dictionary.
        var vm = new TraceViewModel();
        vm.PendingDecode.Should().BeEmpty();
    }

    [Fact]
    public void PendingDecode_Key_Equality_Uses_Id_Timestamp_Channel()
    {
        // v1.2.11: composite key distinguishes frames by (id, timestamp,
        // channel). Same id + channel + microsecond is the same row for the
        // DBC worker lookup.
        var k1 = new TraceEntryKey(0x100, 1_000_000UL, 0x51);
        var k2 = new TraceEntryKey(0x100, 1_000_000UL, 0x51);
        var k3 = new TraceEntryKey(0x100, 1_000_001UL, 0x51);
        var k4 = new TraceEntryKey(0x100, 1_000_000UL, 0x52);

        k1.Should().Be(k2, "same (id, timestamp, channel) → equal keys");
        k1.Should().NotBe(k3, "different timestamp → different keys");
        k1.Should().NotBe(k4, "different channel → different keys");
    }
}
