using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Single-channel assertion context. Migrated from PeakCanAssertionContext.
/// 承担 6 项职责：帧缓冲队列 + 信号缓存 + DBC 解码消费 + recent frames +
/// sink 挂载 + 步骤变量。
///
/// ChannelName 语义：
/// - null（匿名）：接受任何 channelName 作为"自身"（向后兼容，与旧 PeakCanAssertionContext 行为一致）。
/// - 非 null（命名）：channelName 空/空字符串/相等 → 转发到自身；channelName 非空且不等 → 返回空/失败（防误路由）。
/// </summary>
internal sealed class SingleChannelContext : IAssertionContext, IHasRecentFrames, IStepVariableStore, IHasFrameSink, IDisposable
{
    private readonly ICanChannel _channel;
    private readonly IDbcLookup _dbcLookup;
    private readonly ILogger? _logger;
    private readonly Channel<CanFrame> _frameChannel;
    private readonly CancellationTokenSource _consumerCts = new();
    private readonly Task _consumerTask;
    private readonly ConcurrentDictionary<string, (double Value, double TimestampUs)> _signalCache = new();
    private double _currentTimestamp;
    private readonly IDisposable _frameSubscription;
    private ImmutableList<Action<DecodedFrame>> _subscribers = ImmutableList<Action<DecodedFrame>>.Empty;
    private readonly CircularBuffer<CanFrame> _recentFrames = new(capacity: 50);

    /// <summary>逻辑通道名。null = 匿名（接受任何 channelName）。</summary>
    public string? ChannelName { get; }

    /// <summary>物理通道 Id（底层 ICanChannel 的 ChannelId）。</summary>
    public ChannelId ChannelId => _channel.Id;

    /// <summary>底层 ICanChannel 引用（internal，供测试验证多通道模式下默认通道与 DI singleton 共享同一实例）。</summary>
    internal ICanChannel Channel => _channel;

    /// <summary>
    /// 连接底层通道（多通道模式由 MultiChannelAssertionContext.ConnectAllAsync 转发）。
    /// 单通道模式不经过此处（HilRunnerService 直接对默认 ICanChannel.ConnectAsync）。
    /// 返回 ICanChannel.ConnectAsync 的 Result（不再丢弃——review HIGH-1: 首通道
    /// 连接失败必须由调用方显式检查/上报，防静默降级）。
    /// </summary>
    internal Task<Result<Unit>> ConnectAsync(BaudRate? baud, bool fd, CancellationToken ct)
    {
        // null baud = 调用方保证给具体值（ChannelConfig.BaudRate 或 suite 默认）。
        var rate = baud ?? BaudRate.CanFd1Mbps;
        return _channel.ConnectAsync(rate, fd, ct);
    }

    /// <summary>
    /// 断开底层通道（多通道模式由 MultiChannelAssertionContext.DisconnectAllAsync 转发）。
    /// Bug-1：run 结束时所有通道必须 DisconnectAsync，否则非首通道 PCAN handle 泄漏
    /// （PeakCanChannel 只实现 IAsyncDisposable，MS DI 同步 Dispose 不触发 DisposeAsync，
    /// 且 SingleChannelContext.Dispose 只 cancel ConsumerLoop 不调 _channel.DisconnectAsync）。
    /// </summary>
    internal Task DisconnectAsync(CancellationToken ct) => _channel.DisconnectAsync(ct);

    public SingleChannelContext(ICanChannel channel, IDbcLookup dbcLookup, ILogger? logger = null, string? channelName = null)
    {
        _channel = channel;
        _dbcLookup = dbcLookup;
        _logger = logger;
        ChannelName = channelName;
        _frameChannel = System.Threading.Channels.Channel.CreateBounded<CanFrame>(
            new BoundedChannelOptions(10000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = true,
            });
        _frameSubscription = new FrameReceivedSubscription(channel, OnFrame);
        _consumerTask = Task.Run(() => ConsumerLoop(_consumerCts.Token));
    }

    public double CurrentTimestamp => _currentTimestamp;

    public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame)
    {
        ImmutableList<Action<DecodedFrame>> current, updated;
        do
        {
            current = Volatile.Read(ref _subscribers);
            updated = current.Add(onFrame);
        } while (Interlocked.CompareExchange(ref _subscribers, updated, current) != current);

        return new SubscriberSubscription(() =>
        {
            ImmutableList<Action<DecodedFrame>> cur, upd;
            do
            {
                cur = Volatile.Read(ref _subscribers);
                upd = cur.Remove(onFrame);
            } while (Interlocked.CompareExchange(ref _subscribers, upd, cur) != cur);
        });
    }

    /// <summary>按通道名订阅（显式实现 override DIM 默认）。</summary>
    public IDisposable SubscribeDecodedFrames(string? channelName, Action<DecodedFrame> onFrame)
    {
        if (!AcceptsChannelName(channelName))
            return new SubscriberSubscription(() => { }); // 空订阅，回调永不触发
        return SubscribeDecodedFrames(onFrame);
    }

    public double? GetSignalValue(string signalName, int maxAgeMs = 5000)
    {
        if (!_signalCache.TryGetValue(signalName, out var entry))
            return null;

        if (maxAgeMs > 0)
        {
            var ageUs = _currentTimestamp - entry.TimestampUs;
            if (ageUs > maxAgeMs * 1000.0)
                return null;
        }

        return entry.Value;
    }

    /// <summary>按通道名取信号快照（显式实现 override DIM 默认）。null/空/相等名 → 自身；非匹配名 → null。</summary>
    public double? GetSignalValue(string? channelName, string signalName, int maxAgeMs = 5000)
    {
        if (!AcceptsChannelName(channelName))
            return null;   // 非本通道：信号不在本通道缓存，executor 判零样本
        return GetSignalValue(signalName, maxAgeMs);
    }

    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct = default)
    {
        return _channel.WriteAsync(frame, ct);
    }

    /// <summary>按通道名发送（显式实现 override DIM 默认）。</summary>
    /// <para>内填 ChannelId：frame.Channel 被设为本通道的物理 ChannelId，
    /// 这样 executor 无需 cast ctx，case log/报告拿到的帧带正确通道。</para>
    public ValueTask<Result<Unit>> SendFrameAsync(string? channelName, CanFrame frame, CancellationToken ct)
    {
        if (!AcceptsChannelName(channelName))
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidArgument,
                $"Frame send rejected: channelName '{channelName}' does not match this SingleChannelContext ('{ChannelName ?? "<anonymous>"}')"));
        // Fill ChannelId from the underlying channel (executor leaves it default).
        var filled = frame with { Channel = _channel.Id };
        return SendFrameAsync(filled, ct);
    }

    public IReadOnlyList<CanFrame> GetRecentFrames() => _recentFrames.Snapshot();

    // --- IHasFrameSink ---
    // 跨线程：sink 由引擎线程 SetFrameSink 挂载/摘除，consumer 线程读；用 Volatile 保证可见性。
    private IHilFrameSink? _frameSink;

    public void SetFrameSink(IHilFrameSink? sink)
        => Volatile.Write(ref _frameSink, sink);

    /// <summary>按通道名挂载 sink（显式实现 override DIM 默认）。</summary>
    public void SetFrameSink(string? channelName, IHilFrameSink? sink)
    {
        if (!AcceptsChannelName(channelName))
            return;
        SetFrameSink(sink);
    }

    public async Task WaitForFrameDrainAsync(CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(500);
        try
        {
            while (_frameChannel.Reader.Count > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 取消时放弃排空，文件仍合法 */ }
    }

    // IStepVariableStore — 步骤间传值（Phase A）。同 case 内串行执行，无并发写。
    public IDictionary<string, object> Variables { get; } = new Dictionary<string, object>();

    // IAssertionContext.GetRecentDecodedFrames
    private readonly List<DecodedFrame> _decodedRecentFrames = new();
    private readonly object _decodedFramesLock = new();

    public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames()
    {
        lock (_decodedFramesLock) return _decodedRecentFrames.ToList();
    }

    /// <summary>按通道名查最近解码帧（显式实现 override DIM 默认）。</summary>
    public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames(string? channelName)
    {
        if (!AcceptsChannelName(channelName))
            return Array.Empty<DecodedFrame>();
        return GetRecentDecodedFrames();
    }

    public void Dispose()
    {
        // 1. 先取消 consumer loop（阻止处理新帧）
        _consumerCts.Cancel();

        // 2. 再取消 channel 订阅（阻止新帧进入 channel）
        _frameSubscription.Dispose();

        // 3. 等待 consumer 线程退出
        try
        {
            _consumerTask.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected on Cancel
        }
        catch (TimeoutException)
        {
            // Consumer didn't exit in time — continue with best-effort cleanup
        }

        _frameChannel.Writer.Complete();
        _consumerCts.Dispose();
    }

    private void OnFrame(CanFrame frame)
    {
        _currentTimestamp = frame.Timestamp.TotalMicroseconds;
        _frameChannel.Writer.TryWrite(frame);
    }

    private async Task ConsumerLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _frameChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // 逐帧检查取消信号，确保 Cancel 后立即停止调用 subscriber
                ct.ThrowIfCancellationRequested();

                _recentFrames.Add(frame);

                // 所有帧都写 sink（无论 DBC 解码是否成功），G1 约束
                Volatile.Read(ref _frameSink)?.Write(frame);

                var key = DbcLookupKey.ToLookupKey(frame.Id.Raw, frame.Id.IsExtended);
                var message = _dbcLookup.FindMessage(key);

                DecodedFrame decoded;
                // FIND-001 fix: use frame.Timestamp instead of _currentTimestamp.
                // _currentTimestamp is written by OnFrame (producer) and can be overwritten
                // before the consumer processes this frame, causing signal cache to store
                // incorrect timestamps.
                var frameTimestampUs = frame.Timestamp.TotalMicroseconds;

                if (message is not null)
                {
                    var signals = new Dictionary<string, double>();
                    foreach (var signal in message.Signals)
                    {
                        var signalName = $"{message.Name}.{signal.Name}";
                        try
                        {
                            // FIND-004 fix: protect against decode exceptions (e.g.,
                            // ArgumentOutOfRangeException for signal.Length > 64).
                            var value = SignalDecoder.Decode(frame.Data.Span, signal);
                            signals[signalName] = value;
                            _signalCache[signalName] = (value, frameTimestampUs);
                        }
                        catch (Exception ex)
                        {
                            // Log and skip this signal — don't kill the consumer loop.
                            _logger?.LogWarning(ex, "Failed to decode signal {Signal} in message {Message}",
                                signal.Name, message.Name);
                        }
                    }
                    decoded = new DecodedFrame(frame, signals);
                }
                else
                {
                    decoded = new DecodedFrame(frame, new Dictionary<string, double>());
                }

                // Track decoded frames for GetRecentDecodedFrames
                lock (_decodedFramesLock)
                {
                    _decodedRecentFrames.Add(decoded);
                    if (_decodedRecentFrames.Count > 100)
                        _decodedRecentFrames.RemoveRange(0, _decodedRecentFrames.Count - 100);
                }

                var subscribers = Volatile.Read(ref _subscribers);
                foreach (var subscriber in subscribers)
                {
                    try
                    {
                        subscriber(decoded);
                    }
                    catch (Exception)
                    {
                        // Isolate per subscriber
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// 判断给定的 channelName 是否应由本 context 处理。
    /// 匿名 context（ChannelName == null）接受任何 channelName；
    /// 命名 context 只接受 null/空/相等。
    /// </summary>
    private bool AcceptsChannelName(string? channelName)
    {
        // 匿名 context 接受任何 channelName（向后兼容）
        if (ChannelName is null)
            return true;
        // null/空 → 视为"本通道"
        if (string.IsNullOrEmpty(channelName))
            return true;
        // 精确匹配
        return string.Equals(channelName, ChannelName, StringComparison.Ordinal);
    }
}