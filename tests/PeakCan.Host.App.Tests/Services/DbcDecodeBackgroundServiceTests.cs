using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;
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
        var traceVm = new TraceViewModel();
        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, new LiveFrameSlot(), NullLogger<DbcDecodeBackgroundService>.Instance);

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
        var traceVm = new TraceViewModel();
        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, new LiveFrameSlot(), NullLogger<DbcDecodeBackgroundService>.Instance);

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
    /// Build a one-signal DBC matching an Extended message id with the
    /// merged-IDE bit set (bit 31), matching PEAK's
    /// <c>PCAN_MESSAGE_ID</c> convention used by <see cref="DbcParser"/>
    /// and recorded at <see cref="Message.Id"/>.
    /// </summary>
    private static DbcDocument DocWithOneExtendedSignal()
    {
        var sig = new Signal(
            Name: "Ext1", StartBit: 0, Length: 8,
            Order: ByteOrder.LittleEndian,
            ValueType: DbcValueType.Unsigned,
            Factor: 1.0, Offset: 0.0,
            Min: 0, Max: 255, Unit: "u", Receivers: Array.Empty<string>());
        // Merged-IDE form: bit 31 set for Extended (matches PCAN_MESSAGE_ID).
        var mergedId = 0x80000100u;
        var msg = new Message(
            Id: mergedId, Name: "MExt", Dlc: 8, Sender: "n1",
            Signals: new[] { sig },
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        var dict = new Dictionary<uint, Message> { [mergedId] = msg };
        return new DbcDocument(
            Version: "",
            Nodes: Array.Empty<Node>(),
            Messages: new[] { msg },
            MessagesById: dict,
            ValueTables: new Dictionary<string, ValueTable>());
    }

    [Fact]
    public async Task OnFrame_With_Extended_Id_Decodes_When_Dbc_Has_Merged_Ide_Bit()
    {
        // Regression test for the v1.2.4 PATCH extended-frame decode bug:
        // PEAK's CanId.Raw carries 11/29-bit pure IDs (bit 31 = 0), but
        // DbcDocument.MessagesById is keyed by the merged-IDE convention
        // (bit 31 set for Extended, matching PCAN_MESSAGE_ID and
        // DbcParser's BO_ 2147487744 = 0x80001000 acceptance). Without
        // normalizing frame.Id.Raw to the merged-IDE form before lookup,
        // every Extended frame misses the dictionary and SignalViewModel
        // stays empty for any DBC that contains an Extended message.
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneExtendedSignal());
        var sigVm = new SignalViewModel();
        var traceVm = new TraceViewModel();
        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, new LiveFrameSlot(), NullLogger<DbcDecodeBackgroundService>.Instance);

        using var startCts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        try
        {
            // Pure 29-bit ID in Extended format — bit 31 must be 0 per
            // CanId ctor validation (>0x1FFFFFFF throws).
            var frame = new CanFrame(
                new CanId(0x100, FrameFormat.Extended),
                new byte[] { 0x42 },
                FrameFlags.None,
                new ChannelId(0x51),
                Timestamp.FromMicroseconds(0));
            svc.OnFrame(frame);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (sigVm.Latest.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            sigVm.Latest.Should().HaveCount(1, "the matching DBC Extended message must produce one decoded-signal row");
            sigVm.Latest[0].Signal.Should().Be("Ext1");
            sigVm.Latest[0].Message.Should().Be("MExt");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    // --- v1.2.11 PATCH Item 2: DbcDecodeBackgroundService fills TraceEntry.Decoded ---

    [Fact]
    public async Task Worker_Fills_Decoded_Of_Pending_TraceEntry()
    {
        // v1.2.11: when a frame matches DBC AND a pending TraceEntry exists
        // with the same (IdRaw, TimestampMicroseconds, ChannelHandle), the
        // worker must fill entry.Decoded with a "Name=Value, ..." string.
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignal());
        var sigVm = new SignalViewModel();
        var traceVm = new TraceViewModel();
        var entry = new TraceEntry
        {
            Id = new CanId(0x100, FrameFormat.Standard),
            Timestamp = Timestamp.FromMicroseconds(0UL),
            Channel = new ChannelId(0x51),
            DataHex = "42",
        };
        // Inject pending entry directly — RegisterForTesting bypasses the
        // AppendBatchAsync dispatcher path that no-ops without WPF Application.
        traceVm.RegisterForTesting(
            new TraceEntryKey(0x100, 0UL, 0x51), entry);

        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, new LiveFrameSlot(), NullLogger<DbcDecodeBackgroundService>.Instance);

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

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (entry.Decoded == "" && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            entry.Decoded.Should().NotBeEmpty("the matching DBC frame must fill the pending entry's Decoded");
            entry.Decoded.Should().Contain("S1", "the decoded string should name the signal");
            entry.Decoded.Should().Contain("66", "0x42 = 66 unsigned with factor=1.0");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_Buffers_Decoded_Until_Ui_Flush()
    {
        // 8 kfps 下每帧一次 RunOnUiPost 等于 8 000 次/秒的 Dispatcher 投递，
        // 队列无界积压 → 内存持续上涨。worker 只入队，UI 侧定时批量回填。
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignal());
        using var sigVm = new SignalViewModel();
        var traceVm = new TraceViewModel();
        var entry = new TraceEntry
        {
            Id = new CanId(0x100, FrameFormat.Standard),
            Timestamp = Timestamp.FromMicroseconds(0UL),
            Channel = new ChannelId(0x51),
            DataHex = "42",
        };
        traceVm.RegisterForTesting(new TraceEntryKey(0x100, 0UL, 0x51), entry);

        var svc = new DbcDecodeBackgroundService(
            dbc, sigVm, traceVm, new LiveFrameSlot(),
            NullLogger<DbcDecodeBackgroundService>.Instance)
        {
            FlushTimerEnabled = false, // 手动驱动 drain，避免 33ms timer 抢跑
        };

        using var startCts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        try
        {
            svc.OnFrame(new CanFrame(
                new CanId(0x100, FrameFormat.Standard),
                new byte[] { 0x42 },
                FrameFlags.None,
                new ChannelId(0x51),
                Timestamp.FromMicroseconds(0)));

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (svc.PendingDecodedCount == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            svc.PendingDecodedCount.Should().BeGreaterThan(0,
                "worker 应把解码结果放进待回填缓冲，而不是直接投递 UI");
            entry.Decoded.Should().Be("", "UI 侧还没 flush，行不应被改动");

            // 走生产路径：VM 的导出钩子由解码服务在构造时接上 → 触发 flush。
            traceVm.FlushDecodedBeforeExport.Should().NotBeNull(
                "解码服务构造时必须接上导出前的 flush 钩子");
            traceVm.FlushDecodedBeforeExport!();

            entry.Decoded.Should().Contain("S1");
            svc.PendingDecodedCount.Should().Be(0, "flush 之后缓冲应清空");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_Skips_Decoded_For_Evicted_Trace_Entry()
    {
        // 行被 FIFO 淘汰后不再为它排队回填：否则缓冲里留着已经不可见的行引用，
        // 撑到最后也只是做一次没人看的赋值。
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignal());
        using var sigVm = new SignalViewModel();
        var traceVm = new TraceViewModel();
        var entry = new TraceEntry
        {
            Id = new CanId(0x100, FrameFormat.Standard),
            Timestamp = Timestamp.FromMicroseconds(0UL),
            Channel = new ChannelId(0x51),
            DataHex = "42",
        };
        entry.MarkEvicted();
        traceVm.RegisterForTesting(new TraceEntryKey(0x100, 0UL, 0x51), entry);

        var svc = new DbcDecodeBackgroundService(
            dbc, sigVm, traceVm, new LiveFrameSlot(),
            NullLogger<DbcDecodeBackgroundService>.Instance)
        {
            FlushTimerEnabled = false,
        };

        using var startCts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        try
        {
            svc.OnFrame(new CanFrame(
                new CanId(0x100, FrameFormat.Standard),
                new byte[] { 0x42 },
                FrameFlags.None,
                new ChannelId(0x51),
                Timestamp.FromMicroseconds(0)));

            // pending 注册被 worker 出列即说明这一帧已经处理过。
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (traceVm.PendingDecode.Count > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            traceVm.PendingDecode.Count.Should().Be(0,
                "worker 必须在超时前消费这一帧，否则无法区分'跳过'和'没处理'");
            svc.PendingDecodedCount.Should().Be(0, "已淘汰的行不应进入回填缓冲");
            svc.DrainPendingDecodedForTest();
            entry.Decoded.Should().Be("", "已淘汰的行不应再被回填");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Pending_Decoded_Writes_Are_Bounded()
    {
        // UI 彻底卡死时回填缓冲也不能无限增长。
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        using var sigVm = new SignalViewModel();
        var svc = new DbcDecodeBackgroundService(
            dbc, sigVm, new TraceViewModel(), new LiveFrameSlot(),
            NullLogger<DbcDecodeBackgroundService>.Instance);

        for (var i = 0; i < 20_500; i++)
            svc.EnqueueDecodedForTest(new TraceEntry(), "x");

        svc.PendingDecodedCount.Should().BeLessThanOrEqualTo(20_000);
        svc.DroppedDecodedWrites.Should().BeGreaterThan(0, "超出上限的写入应被丢弃并计数");
    }

    [Fact]
    public async Task Worker_NoOp_When_Frame_Not_In_Dbc()
    {
        // v1.2.11: if frame id is not in DBC, the worker skips the lookup
        // entirely and pending entry's Decoded stays "".
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignal());  // only id 0x100
        var sigVm = new SignalViewModel();
        var traceVm = new TraceViewModel();
        var entry = new TraceEntry
        {
            Id = new CanId(0x555, FrameFormat.Standard),
            Timestamp = Timestamp.FromMicroseconds(0UL),
            Channel = new ChannelId(0x51),
            DataHex = "42",
        };
        traceVm.RegisterForTesting(
            new TraceEntryKey(0x555, 0UL, 0x51), entry);

        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, new LiveFrameSlot(), NullLogger<DbcDecodeBackgroundService>.Instance);

        using var startCts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        try
        {
            var frame = new CanFrame(
                new CanId(0x555, FrameFormat.Standard),
                new byte[] { 0x42 },
                FrameFlags.None,
                new ChannelId(0x51),
                Timestamp.FromMicroseconds(0));
            svc.OnFrame(frame);

            await Task.Delay(200);   // give worker time to process (and skip)

            entry.Decoded.Should().Be("", "frames not in DBC must not touch Decoded");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_NoThrow_When_Pending_Missing()
    {
        // v1.2.11: if frame is in DBC but no pending entry exists, the worker
        // must not throw — SignalViewModel still receives the decoded signal.
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignal());
        var sigVm = new SignalViewModel();
        var traceVm = new TraceViewModel();   // empty — no pending entries

        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, new LiveFrameSlot(), NullLogger<DbcDecodeBackgroundService>.Instance);

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

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (sigVm.Latest.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            sigVm.Latest.Should().HaveCount(1, "SignalViewModel still receives the decoded signal");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    // ===== v1.2.12 PATCH Item 11: sink OnError → ILogger =====

    [Fact]
    public void OnError_Calls_ILogger_LogWarning()
    {
        // Source-gen [LoggerMessage] gates Log() on IsEnabled, so stub true.
        var logger = Substitute.For<ILogger<DbcDecodeBackgroundService>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        var sigVm = new SignalViewModel();
        var traceVm = new TraceViewModel();
        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, new LiveFrameSlot(), logger);
        var ex = new InvalidOperationException("test");

        svc.OnError(ex);

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            ex,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Matching_Frame_Is_Cached_In_LiveSlot()
    {
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignal());
        var slot = new LiveFrameSlot();
        slot.SetTarget(0x100);
        var svc = new DbcDecodeBackgroundService(
            dbc, new SignalViewModel(), new TraceViewModel(), slot,
            NullLogger<DbcDecodeBackgroundService>.Instance);

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

            LiveFramePayload payload = default;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!slot.TryRead(0x100, out payload) && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            slot.TryRead(0x100, out payload).Should().BeTrue("匹配帧必须进入单槽缓存");
            payload.Data.Span.ToArray().Should().Equal(new byte[] { 0x42 });
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    private static DbcDocument DocWithOneSignalAtExtendedId()
    {
        var sig = new Signal(
            Name: "S1", StartBit: 0, Length: 8,
            Order: ByteOrder.LittleEndian,
            ValueType: DbcValueType.Unsigned,
            Factor: 1.0, Offset: 0.0,
            Min: 0, Max: 255, Unit: "u", Receivers: Array.Empty<string>());
        var msg = new Message(
            Id: 0x80001000, Name: "M1", Dlc: 8, Sender: "n1",
            Signals: new[] { sig },
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        var dict = new Dictionary<uint, Message> { [0x80001000] = msg };
        return new DbcDocument(
            Version: "",
            Nodes: Array.Empty<Node>(),
            Messages: new[] { msg },
            MessagesById: dict,
            ValueTables: new Dictionary<string, ValueTable>());
    }

    [Fact]
    public async Task Extended_Frame_Is_Cached_Under_Merged_Ide_Id()
    {
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignalAtExtendedId());
        var slot = new LiveFrameSlot();
        slot.SetTarget(0x80001000);
        var svc = new DbcDecodeBackgroundService(
            dbc, new SignalViewModel(), new TraceViewModel(), slot,
            NullLogger<DbcDecodeBackgroundService>.Instance);

        using var startCts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        try
        {
            var frame = new CanFrame(
                new CanId(0x1000, FrameFormat.Extended),
                new byte[] { 0x7E },
                FrameFlags.None,
                new ChannelId(0x51),
                Timestamp.FromMicroseconds(0));
            svc.OnFrame(frame);

            LiveFramePayload payload = default;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!slot.TryRead(0x80001000, out payload) && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            slot.TryRead(0x80001000, out payload).Should()
                .BeTrue("扩展帧必须按合并 IDE 位后的消息 ID 进单槽");
            payload.Data.Span.ToArray().Should().Equal(new byte[] { 0x7E });
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }
}
