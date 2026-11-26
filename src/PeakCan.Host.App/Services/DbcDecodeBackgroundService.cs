using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Services;

/// <summary>
/// Consumes raw <see cref="CanFrame"/>s on the SDK read thread
/// (via <see cref="IFrameSink.OnFrame"/>) and runs the DBC lookup +
/// signal decode on its own worker thread, offloading work that
/// previously ran inline on the read loop.
/// <para>
/// <b>Why a service?</b> the SDK read loop must stay non-blocking at
/// ~8 000 fps; any per-frame dictionary lookup + signal-decode +
/// collection mutation budget eats into that. The offload path uses a
/// bounded <see cref="Channel{T}"/> with <see cref="BoundedChannelFullMode.DropOldest"/>
/// so back-pressure is bounded — if the worker falls behind, oldest
/// frames are dropped rather than ballooning memory.
/// </para>
/// <para>
/// <b>Lifecycle:</b> the service implements both <see cref="IFrameSink"/>
/// (for the SDK-thread <see cref="OnFrame"/> call) and
/// <see cref="BackgroundService"/> (so <see cref="PeakCan.Host.App.Composition.SinkWiringService"/> can
/// attach it to the <c>ChannelRouter</c> and so the worker
/// thread starts on <see cref="BackgroundService.StartAsync"/>).
/// </para>
/// </summary>
public sealed partial class DbcDecodeBackgroundService : BackgroundService, IFrameSink
{
    /// <summary>Bounded capacity; matches TraceService's drop semantics.</summary>
    private const int DecodeQueueCapacity = 10_000;

    private readonly DbcService _dbc;
    private readonly SignalViewModel _signalVm;
    private readonly TraceViewModel _traceVm;
    private readonly LiveFrameSlot _liveSlot;
    private readonly ILogger<DbcDecodeBackgroundService> _logger;
    private readonly Channel<CanFrame> _queue = Channel.CreateBounded<CanFrame>(
        new BoundedChannelOptions(DecodeQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>待回填解码文本的硬上限：UI 严重落后时丢弃新结果，不再占内存。</summary>
    private const int MaxPendingDecodedWrites = 20_000;

    // key 是行引用（TraceEntry 未重写 Equals → 引用相等）。用字典缓冲而不是
    // 每帧一次 Dispatcher.InvokeAsync：8 kfps 就是 8 000 次/秒投递，Dispatcher
    // 队列无界积压 → 内存持续上涨。v1.2.3 在 SignalViewModel 修过同类问题，
    // trace 回填这条支路当时漏了。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TraceEntry, string> _pendingDecoded = new();
    private long _droppedDecodedWrites;

    /// <summary>测试接缝：关闭自动 UI flush，让测试手动驱动 drain。</summary>
    internal bool FlushTimerEnabled { get; set; } = true;

    /// <summary>测试接缝：待回填条目数。</summary>
    internal int PendingDecodedCount => _pendingDecoded.Count;

    /// <summary>测试接缝：因缓冲满被丢弃的回填条数。</summary>
    internal long DroppedDecodedWrites => Interlocked.Read(ref _droppedDecodedWrites);

    /// <summary>
    /// UI 线程调用：立即把缓冲里的解码文本写回行。导出等"需要立刻拿到最新
    /// 解码结果"的场景用它，否则最多落后一个 33ms 窗口。
    /// </summary>
    public void FlushPendingDecoded()
    {
        // 必须 UI 线程调用：Decoded 的 setter 会触发 INPC，跨线程会被 WPF
        // 绑定层拒绝。当前调用方（导出、33ms 定时器经 RunOnUiPost）都在 UI 线程。
        System.Diagnostics.Debug.Assert(
            System.Windows.Application.Current?.Dispatcher.CheckAccess() ?? true,
            "FlushPendingDecoded must run on the WPF UI thread");
        DrainPendingDecoded();
    }

    /// <summary>测试接缝：同步执行一次 UI 侧回填。</summary>
    internal void DrainPendingDecodedForTest() => FlushPendingDecoded();

    /// <summary>测试接缝：直接入队一条回填结果（验证有界性）。</summary>
    internal void EnqueueDecodedForTest(TraceEntry entry, string decoded) => EnqueueDecoded(entry, decoded);

    public DbcDecodeBackgroundService(
        DbcService dbc,
        SignalViewModel signalVm,
        TraceViewModel traceVm,
        LiveFrameSlot liveSlot,
        ILogger<DbcDecodeBackgroundService> logger)
    {
        _dbc = dbc ?? throw new ArgumentNullException(nameof(dbc));
        _signalVm = signalVm ?? throw new ArgumentNullException(nameof(signalVm));
        _traceVm = traceVm ?? throw new ArgumentNullException(nameof(traceVm));
        _liveSlot = liveSlot ?? throw new ArgumentNullException(nameof(liveSlot));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 导出前先把缓冲里的解码结果落地，否则 CSV 的 Decoded 列会缺最近
        // 一个 33ms 窗口。VM 不反向依赖本服务，所以由服务构造时接上。
        _traceVm.FlushDecodedBeforeExport = FlushPendingDecoded;
    }

    /// <summary>
    /// SDK-thread entrypoint. Non-blocking: enqueues into the bounded
    /// channel. If the worker is behind, the oldest frame is silently
    /// dropped (consistent with TraceService's policy).
    /// </summary>
    public void OnFrame(CanFrame frame) => _queue.Writer.TryWrite(frame);

    /// <summary>Sink-isolation hook — logs via ILogger. Debug.WriteLine stripped in Release builds; ILogger is not.</summary>
    public void OnError(Exception ex)
    {
        LogSinkError(_logger, ex, nameof(DbcDecodeBackgroundService));
    }

    /// <summary>
    /// Worker loop: drain the queue and run the DBC decode on this
    /// service's thread. <see cref="SignalViewModel.ApplyFrame"/>
    /// marshals the resulting collection mutation to the WPF UI thread.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 33ms 批量回填：每帧一次 UI 投递 → worker 只入队、≤30 次/秒统一应用。
        System.Threading.Timer? flushTimer = FlushTimerEnabled
            ? new System.Threading.Timer(
                _ => ((Action)DrainPendingDecoded).RunOnUiPost(),
                state: null,
                dueTime: TimeSpan.FromMilliseconds(33),
                period: TimeSpan.FromMilliseconds(33))
            : null;

        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_queue.Reader.TryRead(out var frame))
                {
                    var doc = _dbc.Current;
                    if (doc is null) continue;
                    // PEAK's CanId.Raw carries the pure 11/29-bit
                    // identifier (bit 31 = 0, enforced by CanId ctor
                    // validation), but DbcDocument.MessagesById is keyed
                    // by the merged-IDE convention (bit 31 set for
                    // Extended, matching PCAN_MESSAGE_ID). The DBC parser
                    // stores Extended message IDs with the IDE bit set,
                    // e.g. "BO_ 2147487744 X" lands as 0x80001000.
                    // Without this normalization, every Extended frame
                    // misses the dictionary and SignalViewModel stays
                    // empty for any DBC containing an Extended message.
                    //
                    // NOTE: the scripting API
                    // (Scripting/CanApi.cs:217 _messageCallbacks lookup)
                    // intentionally uses the opposite convention — raw
                    // ID, no IDE bit merge — because there script
                    // authors supply the int literal directly. Do not
                    // "consistency-fix" that lookup without revisiting
                    // the script API contract.
                    var lookupId = frame.Id.IsExtended
                        ? frame.Id.Raw | 0x80000000u
                        : frame.Id.Raw;
                    if (!doc.MessagesById.TryGetValue(lookupId, out var msg)) continue;
                    _signalVm.ApplyFrame(frame, msg);

                    // 二期：只缓存当前选中消息的原始载荷（单槽，非选中消息零拷贝）。
                    _liveSlot.Publish(msg.Id, frame.Data.Span);

                    // v1.2.11 PATCH Item 2 fan-out: if a TraceEntry is
                    // awaiting DBC decode for this frame, fill its Decoded
                    // string. The lookup key matches the (IdRaw,
                    // TimestampMicroseconds, ChannelHandle) tuple that
                    // TraceViewModel.AppendBatchAsync registered — pure ID
                    // without IDE-merge, since that's what was registered.
                    var pendingKey = new TraceEntryKey(
                        frame.Id.Raw,
                        frame.Timestamp.TotalMicroseconds,
                        frame.Channel.Handle);
                    // TryCompletePending atomically removes the entry on
                    // success so it doesn't linger in the pending map for
                    // the lifetime of the trace (v1.2.11 code review HIGH).
                    if (_traceVm.TryCompletePending(pendingKey, out var traceEntry) && traceEntry is not null)
                    {
                        // worker 只入队；UI 侧 33ms 定时批量写回并触发 INPC。
                        // 此前是每帧一次 RunOnUiPost（8 kfps → 8 000 次/秒），
                        // Dispatcher 队列无界积压，内存和响应一起恶化。
                        EnqueueDecoded(traceEntry, FormatDecoded(msg, frame));
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        finally
        {
            flushTimer?.Dispose();
            // LOW fix: complete the writer on shutdown. Without this,
            // OnFrame can still write to the channel after the worker
            // exits, violating the Channel contract. TryWrite succeeds
            // silently but the data is never consumed.
            _queue.Writer.TryComplete();
        }
    }

    /// <summary>
    /// worker 线程调用：只入队，不碰 UI、不碰行属性。已经离开表格的行不再
    /// 保留引用；缓冲达到上限时丢弃新结果（计数可见），保证内存有界。
    /// </summary>
    private void EnqueueDecoded(TraceEntry entry, string decoded)
    {
        if (entry.IsEvicted) return;

        if (_pendingDecoded.Count >= MaxPendingDecodedWrites)
        {
            // 有丢弃必须可见（对齐 TraceService 的限频计数模式）。
            var dropped = Interlocked.Increment(ref _droppedDecodedWrites);
            if (dropped % 100 == 1) LogDecodedWritesDropped(_logger, dropped);
            return;
        }

        _pendingDecoded[entry] = decoded;
    }

    /// <summary>
    /// UI 线程执行：把积压的解码文本一次性写回行（触发一次 INPC 由
    /// <see cref="TraceEntry.Decoded"/> 的 setter 负责）。取出即移除引用；
    /// 已淘汰的行跳过，不为已经不存在的行发通知。
    /// </summary>
    private void DrainPendingDecoded()
    {
        if (_pendingDecoded.IsEmpty) return;

        foreach (var pair in _pendingDecoded)
        {
            if (!_pendingDecoded.TryRemove(pair.Key, out var decoded)) continue;
            if (pair.Key.IsEvicted) continue;
            pair.Key.Decoded = decoded;
        }
    }

    /// <summary>
    /// v1.2.11 PATCH Item 2: format a DBC message's decoded signals as
    /// "Name=Value, Name=Value, ...". Uses invariant culture so the
    /// Trace Decoded column reads consistently across locales.
    /// </summary>
    internal static string FormatDecoded(Message msg, CanFrame frame)
    {
        if (msg.Signals.Count == 0) return string.Empty;
        var parts = new List<string>(msg.Signals.Count);
        foreach (var signal in msg.Signals)
        {
            var value = SignalDecoder.Decode(frame.Data.Span, signal);
            // G format keeps doubles compact (no trailing zeros); InvariantCulture
            // prevents de-DE locale from emitting "0,42" with a comma.
            parts.Add($"{signal.Name}={value.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        return string.Join(", ", parts);
    }

    // v1.2.12 PATCH Item 11: sink OnError → ILogger. The previous
    // Debug.WriteLine was stripped in Release builds, leaving production
    // with no record of forwarded errors. EventId 6002.
    [LoggerMessage(EventId = 6002, Level = LogLevel.Warning, Message = "{Service} OnError forwarded")]
    private static partial void LogSinkError(ILogger logger, Exception ex, string service);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Warning,
        Message = "Decoded write buffer full; {Dropped} writes dropped since startup")]
    private static partial void LogDecodedWritesDropped(ILogger logger, long dropped);
}
