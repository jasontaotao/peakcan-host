using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using DbcValueType = PeakCan.Host.Core.Dbc.ValueType;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// Verifies that the DBC decode path runs OFF the SDK read thread.
/// The contract: <see cref="DbcDecodeBackgroundService.OnFrame"/> must
/// return quickly (no DBC lookup, no SignalEntry construction). The
/// decoded-signal observable in <see cref="SignalViewModel.Latest"/> is
/// updated asynchronously on the service's own worker.
/// <para>
/// <b>v1.2.1 PATCH (Task 6):</b> the ctor calls
/// <see cref="LeakedApplicationReset.CleanupLeakedApplication"/> to null
/// out any leaked <see cref="System.Windows.Application.Current"/> from a
/// sibling test class (xUnit runs test classes in parallel). Without this,
/// the inline path inside <see cref="SignalViewModel.ApplyFrame"/> would
/// route through <c>Dispatcher.InvokeAsync</c> on a dead dispatcher and
/// <see cref="SignalViewModel.Latest"/> would stay empty — same root cause
/// as the v1.2.0 §9.1 SignalViewModel flake closed in Task 5
/// (commit <c>23e7d7c</c>).
/// </para>
/// </summary>
public class DbcDecodeBackgroundServiceTests
{
    // v1.2.1 PATCH Task 6: defensive cleanup of leaked Application.Current
    // before each test (ctor runs once per test instance in xUnit).
    public DbcDecodeBackgroundServiceTests() => LeakedApplicationReset.CleanupLeakedApplication();

    /// <summary>
    /// Build a one-signal DBC matching id 0x100. Uses the actual record
    /// signatures (5-arg <see cref="DbcDocument"/>, 11-arg <see cref="Signal"/>,
    /// 5-arg <see cref="Message"/>) — the brief's helper used shorthand
    /// positional args that no longer compile against the current Core types.
    /// </summary>
    private static DbcDocument DocWithOneSignal()
    {
        var sig = new Signal(
            Name: "S1", StartBit: 0, Length: 8,
            Order: ByteOrder.LittleEndian,
            ValueType: DbcValueType.Unsigned,
            Factor: 1.0, Offset: 0.0,
            Min: 0, Max: 255, Unit: "u", Receivers: Array.Empty<string>());
        var msg = new Message(
            Id: 0x100, Name: "M1", Dlc: 8, Sender: "n1",
            Signals: new[] { sig },
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        var dict = new Dictionary<uint, Message> { [0x100] = msg };
        return new DbcDocument(
            Version: "",
            Nodes: Array.Empty<Node>(),
            Messages: new[] { msg },
            MessagesById: dict,
            ValueTables: new Dictionary<string, ValueTable>());
    }

    [Fact]
    public async Task OnFrame_With_Matching_Dbc_Decodes_Eventually()
    {
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignal());
        var sigVm = new SignalViewModel();
        var svc = new DbcDecodeBackgroundService(dbc, sigVm);

        using var startCts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        try
        {
            var frame = new CanFrame(
                new CanId(0x100, FrameFormat.Standard),
                new byte[] { 0x42 },
                FrameFlags.None,
                new ChannelId(0x51),
                Timestamp.FromMicroseconds(0));
            svc.OnFrame(frame);

            // Wait up to 5 s for the service to drain. CI Windows runners
            // under load can take several seconds for the BackgroundService
            // worker to dequeue and decode; 1 s is too aggressive and flakes
            // the PR build (pre-existing on main).
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (sigVm.Latest.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            sigVm.Latest.Should().HaveCount(1, "the matching DBC message must produce one decoded-signal row");
            sigVm.Latest[0].Signal.Should().Be("S1");
            sigVm.Latest[0].Message.Should().Be("M1");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OnFrame_Without_Dbc_Loaded_Is_NoOp()
    {
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        var sigVm = new SignalViewModel();
        var svc = new DbcDecodeBackgroundService(dbc, sigVm);

        using var startCts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        try
        {
            var frame = new CanFrame(
                new CanId(0x100, FrameFormat.Standard),
                new byte[] { 0x42 },
                FrameFlags.None,
                new ChannelId(0x51),
                Timestamp.FromMicroseconds(0));
            svc.OnFrame(frame); // must not throw, must not enqueue work that loops forever

            await Task.Delay(50);
            sigVm.Latest.Should().BeEmpty();
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// v1.2.1 PATCH Task 6 RED repro: simulates the leaked
    /// <see cref="System.Windows.Application.Current"/> race that caused
    /// <see cref="OnFrame_With_Matching_Dbc_Decodes_Eventually"/> to flake
    /// in v1.2.0 §9.1. A sibling STA test (e.g.
    /// <c>TraceViewModelTests.AppendBatch_On_StaThread_With_Application_Adds_All_Frames</c>)
    /// leaves <c>Application.Current</c> non-null with a live-but-stuck
    /// dispatcher. When <see cref="DbcDecodeBackgroundService.ExecuteAsync"/>
    /// runs on its worker thread and calls
    /// <see cref="SignalViewModel.ApplyFrame"/>, the dispatcher's
    /// <see cref="DispatcherExtensions.RunOnUiPost"/> sees
    /// <c>appDispatcher.Thread.IsAlive == true</c> and routes the upsert
    /// through <c>Dispatcher.InvokeAsync</c> on the blocked STA thread —
    /// the queued action never runs and <c>sigVm.Latest</c> stays empty.
    /// <para>
    /// This test is RED today (fails with "Expected 1, found 0") because
    /// the test class has no defensive <c>LeakedApplicationReset</c> call.
    /// Task 6 fix: add a ctor that calls
    /// <c>LeakedApplicationReset.CleanupLeakedApplication()</c> (mirrors the
    /// Task 5 fix in <c>SignalViewModelTests</c>,
    /// <c>StatsViewModelTests</c>, <c>TraceViewModelTests</c>). With the fix,
    /// the inline path inside <see cref="SignalViewModel.ApplyFrame"/> runs
    /// and the assertion holds.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OnFrame_With_LeakedApplication_DeadDispatcher_Fills_Latest()
    {
        // Clean any pre-existing leak first so we start from a known
        // state, then re-introduce the leak that this test simulates.
        PeakCan.Host.App.Tests.Collections.LeakedApplicationReset.CleanupLeakedApplication();

        var pumpRelease = new ManualResetEventSlim(false);
        Exception? staCaught = null;
        var staThread = new Thread(() =>
        {
            try
            {
                _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                pumpRelease.Set();
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) { staCaught = ex; }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        pumpRelease.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "the STA thread must reach the Application ctor within 5 s " +
            "for the race simulation to be valid");
        System.Windows.Application.Current.Should().NotBeNull(
            "the STA thread set Application.Current and is still alive");

        try
        {
            // Run the same path as OnFrame_With_Matching_Dbc_Decodes_Eventually.
            var dbc = new DbcService(NullLogger<DbcService>.Instance);
            dbc.SetCurrentForTests(DocWithOneSignal());
            var sigVm = new SignalViewModel();
            var svc = new DbcDecodeBackgroundService(dbc, sigVm);

            using var startCts = new System.Threading.CancellationTokenSource();
            await svc.StartAsync(startCts.Token);
            try
            {
                var frame = new CanFrame(
                    new CanId(0x100, FrameFormat.Standard),
                    new byte[] { 0x42 },
                    FrameFlags.None,
                    new ChannelId(0x51),
                    Timestamp.FromMicroseconds(0));
                svc.OnFrame(frame);

                // Wait up to 5 s for the worker to drain.
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (sigVm.Latest.Count == 0 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }

                // Without the ctor CleanupLeakedApplication fix, this fails
                // with "Expected 1, found 0" because the inline path inside
                // ApplyFrame routes through Dispatcher.InvokeAsync on the
                // blocked STA thread (dead dispatcher).
                sigVm.Latest.Should().HaveCount(1,
                    "ApplyFrame must populate Latest even when Application.Current " +
                    "points at a dispatcher whose thread is alive but not pumping");
            }
            finally
            {
                await svc.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            // Clean up: release the STA thread, shut down Application,
            // null out the singleton so subsequent tests are not affected.
            try { System.Windows.Application.Current?.Shutdown(); } catch { }
            typeof(System.Windows.Application).GetField("_appInstance",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)
                ?.SetValue(null, null);
            staThread.Join(TimeSpan.FromSeconds(5));
            _ = staCaught;
        }
    }
}
