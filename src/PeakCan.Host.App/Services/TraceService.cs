using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Services;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Services;

/// <summary>
/// <see cref="BackgroundService"/> that batches inbound frames into
/// <see cref="TraceViewModel"/> every 50 ms. Implements
/// <see cref="IFrameSink"/> so the <see cref="ChannelRouter"/> can
/// attach it as a fan-out target.
/// <para>
/// <b>Why batch?</b> a 1 Mbps CAN bus can sustain ~8 000 fps; updating
/// an <c>ObservableCollection</c> per frame would flood the WPF
/// dispatcher with <c>CollectionChanged</c> notifications and peg a
/// CPU. Batching at 50 ms caps the UI work at 20 updates/sec,
/// independent of bus rate.
/// </para>
/// <para>
/// <b>Why a bounded channel?</b> <see cref="OnFrame"/> is called on the
/// SDK read thread; it must be non-blocking. A bounded
/// <see cref="Channel{T}"/> with <see cref="BoundedChannelFullMode.DropOldest"/>
/// gives us natural back-pressure: if the UI stalls for &gt;50 ms the
/// oldest unread frames are dropped rather than ballooning memory.
/// </para>
/// <para>
/// <b>Thread-safety:</b> the SDK read thread calls
/// <see cref="OnFrame"/>; the <see cref="ExecuteAsync(CancellationToken)"/>
/// loop drains the channel and dispatches to the UI thread. The
/// channel itself is the synchronization point — no shared state
/// outside the channel is touched from both threads.
/// </para>
/// <para>
/// <b>DBC decode offload:</b> the per-frame dictionary lookup +
/// signal decode + collection mutation that previously ran inline on
/// the SDK read thread is owned by
/// <see cref="DbcDecodeBackgroundService"/>, which fans out the same
/// frame via its own bounded channel. TraceService is now a pure
/// frame forwarder with respect to the trace VM.
/// </para>
/// </summary>
public sealed partial class TraceService : BackgroundService, IFrameSink
{
    /// <summary>Bounded channel capacity. At 1 Mbps / 8 byte classic, 10 000 frames ≈ 1.25 s of buffering.</summary>
    private const int BatchCapacity = 10_000;

    private readonly TraceViewModel _vm;
    private readonly ILogger<TraceService> _logger;
    // v3.5.3 PATCH: hold the factory, not a concrete PeriodicTimer, so
    // unit tests can drive the 200ms batching tick deterministically
    // (see FakeTimerFactory in App.Tests). Default ctor wires a real
    // PeriodicTimer-backed factory for production; internal ctor lets
    // tests inject a fake. Mirrors the v3.5.2 pattern in RecordService
    // + StatisticsService — closes the third (and most painful)
    // BackgroundService that depended on a wall-clock PeriodicTimer and
    // caused TraceServiceTests.ExecuteAsync_Periodically_Flushes_Channel_Into_VM_Batch
    // to hang the xunit test host under parallel execution since v1.7.x.
    private readonly ITimerFactory _timerFactory;
    private readonly Channel<CanFrame> _batch = Channel.CreateBounded<CanFrame>(
        new BoundedChannelOptions(BatchCapacity) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>
    /// Approximate count of frames dropped because the bounded channel
    /// was full at the time of <see cref="OnFrame"/>. Spec §6.2 mandates
    /// "dropped frames only log" — we emit a Debug.WriteLine every 100
    /// drops so a debugger-attached host sees the overruns without
    /// flooding the log at 8 kfps. Surfacing this to the UI status bar
    /// is a follow-up.
    /// </summary>
    private long _droppedOnFullChannel;

    /// <summary>Read-only accessor for the drop counter (tests + future UI).</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedOnFullChannel);

    /// <summary>
    /// Construct a service bound to <paramref name="vm"/>. The DBC
    /// decode fan-out is wired separately via
    /// <see cref="DbcDecodeBackgroundService"/>. <paramref name="logger"/>
    /// is optional only for backward compatibility with existing test
    /// fixtures that do not assert on the logger; production DI always
    /// passes one.
    /// </summary>
    public TraceService(TraceViewModel vm, ILogger<TraceService>? logger = null)
        : this(vm, logger, new PeriodicTimerFactory())
    {
    }

    /// <summary>
    /// v3.5.3 PATCH: internal ctor lets unit tests inject an
    /// <see cref="ITimerFactory"/> (typically a <c>FakeTimerFactory</c>)
    /// so the 200ms batching tick can be driven deterministically without
    /// a real <see cref="System.Threading.PeriodicTimer"/>. Production DI
    /// uses the public 2-arg ctor above, which constructs a real
    /// <see cref="PeriodicTimerFactory"/>. Mirrors the v3.5.2 pattern in
    /// <see cref="RecordService"/> + <see cref="StatisticsService"/>.
    /// </summary>
    internal TraceService(TraceViewModel vm, ILogger<TraceService>? logger, ITimerFactory timerFactory)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _logger = logger ?? NullLogger<TraceService>.Instance;
        _timerFactory = timerFactory ?? throw new ArgumentNullException(nameof(timerFactory));
    }

    /// <summary>
    /// Background loop: every 200 ms (via <see cref="PeriodicTimer"/>),
    /// drain the channel into a local list and hand it to the VM.
    /// Exits cleanly on cancellation — PeriodicTimer.WaitForNextTickAsync
    /// throws <see cref="OperationCanceledException"/> on token cancel,
    /// which propagates and ends the loop without an explicit break.
    /// <para>
    /// <b>Tick interval (200 ms vs 50 ms):</b> at high frame rates (≥
    /// 1 000 fps), a 50 ms tick fires the WPF dispatcher 20 times/sec
    /// with batches of ~50 frames each; under sustained load the
    /// <see cref="ObservableCollection{T}"/> mutations + DataGrid item
    /// container creation saturate the UI thread and the grid becomes
    /// unresponsive. Bumping the tick to 200 ms caps dispatcher
    /// invocations at 5/sec with batches of ~200 frames; the absolute
    /// work is unchanged but the layout passes collapse, leaving the
    /// UI thread free to process paint + scroll between batches.
    /// </para>
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // v3.5.3 PATCH: factory-supplied timer so the 200ms tick is
        // test-deterministic (FakeTimerFactory.Fire()) when wired by
        // the internal test-only ctor. await using because IPeriodicTimer
        // is IAsyncDisposable (PeriodicTimer dispose is sync but the
        // IAsyncDisposable contract is required of any async-capable
        // timer). Same pattern as RecordService.ExecuteAsync (v3.5.2).
        await using var timer = _timerFactory.CreateTimer(TimeSpan.FromMilliseconds(200));
        var buf = new List<CanFrame>(1024);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                buf.Clear();
                while (_batch.Reader.TryRead(out var f)) buf.Add(f);
                if (buf.Count > 0) await _vm.AppendBatchAsync(buf);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    /// <summary>
    /// Receives a frame from the <see cref="ChannelRouter"/>. Non-throwing,
    /// non-blocking: <see cref="ChannelWriter{T}.TryWrite"/> on a bounded
    /// <c>DropOldest</c> channel always succeeds (it silently drops the
    /// oldest unread item if the channel is full). Drops are counted and
    /// logged every 100th occurrence per spec §6.2.
    /// <para>
    /// Pure forwarder: DBC lookup and signal decode moved to
    /// <see cref="DbcDecodeBackgroundService"/> so this method does no
    /// per-frame dictionary reads or VM mutation on the SDK read thread.
    /// </para>
    /// </summary>
    public void OnFrame(CanFrame frame)
    {
        if (_batch.Reader.Count >= BatchCapacity)
        {
            // Pre-check: if the channel is at capacity, the next TryWrite
            // will trigger DropOldest. We count this as a drop event.
            // There is a benign race where ExecuteAsync drains between
            // the check and the write (false positive) — acceptable for
            // an MVP diagnostic counter.
            var n = Interlocked.Increment(ref _droppedOnFullChannel);
            if (n % 100 == 1)
            {
                Debug.WriteLine(
                    $"[TraceService] channel full; dropped ~{n} frames since startup (capacity={BatchCapacity})");
            }
        }
        _batch.Writer.TryWrite(frame);
    }

    /// <summary>
    /// Sink-isolation contract: <see cref="ChannelRouter"/> forwards
    /// exceptions from peer sinks here. The trace pipeline is a
    /// downstream consumer — the originating sink's failure is the
    /// router's concern, not ours. TraceService no longer participates
    /// in the DBC-decode fan-out (that work moved to
    /// <see cref="DbcDecodeBackgroundService"/>); this hook logs the
    /// forwarded error via ILogger so Release builds have a record.
    /// </summary>
    public void OnError(Exception ex)
    {
        LogSinkError(_logger, ex, nameof(TraceService));
    }

    // v1.2.12 PATCH Item 11: sink OnError → ILogger. The previous
    // Debug.WriteLine was stripped in Release builds, leaving production
    // with no record of forwarded errors. EventId 6003.
    [LoggerMessage(EventId = 6003, Level = LogLevel.Warning, Message = "{Service} OnError forwarded")]
    private static partial void LogSinkError(ILogger logger, Exception ex, string service);
}
