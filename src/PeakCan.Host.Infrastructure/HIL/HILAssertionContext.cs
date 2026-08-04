using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Bridges a virtual CAN channel to the HIL assertion context.
/// Subscribes to channel.FrameReceived, decodes frames via DBC, caches signal values.
/// </summary>
internal sealed class HILAssertionContext : IAssertionContext, IFaultInjectionContext, IHasRecentFrames, IStepVariableStore, IDisposable
{
    private readonly ICanChannel _channel;
    private readonly ICanChannel _effectiveChannel; // FaultInjector wrapper or raw channel
    private readonly FaultInjector? _faultInjector;
    private readonly ReceivePathFaultInjector? _receiveFaultInjector;
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
    private readonly ConcurrentDictionary<string, IDisposable> _faultHandles = new();

    public HILAssertionContext(ICanChannel channel, IDbcLookup dbcLookup, bool enableFaultInjection = false, ILogger? logger = null)
    {
        _channel = channel;
        _dbcLookup = dbcLookup;
        _logger = logger;

        // When fault injection is enabled, wrap channel with FaultInjector (send path)
        // and ReceivePathFaultInjector (receive path).
        if (enableFaultInjection)
        {
            _faultInjector = new FaultInjector(channel);
            _receiveFaultInjector = new ReceivePathFaultInjector(_faultInjector);
            _effectiveChannel = _receiveFaultInjector;
        }
        else
        {
            _effectiveChannel = channel;
        }

        _frameChannel = System.Threading.Channels.Channel.CreateBounded<CanFrame>(
            new BoundedChannelOptions(10000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = true,
            });
        _frameSubscription = new FrameReceivedSubscription(_effectiveChannel, OnFrame);
        _consumerTask = Task.Run(() => ConsumerLoop(_consumerCts.Token));
    }

    public double CurrentTimestamp => _currentTimestamp;

    public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame)
    {
        // Thread-safe add: spin until Interlocked.Exchange succeeds
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

    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct = default)
    {
        // Delegate to effective channel (FaultInjector wrapper or raw channel)
        return _effectiveChannel.WriteAsync(frame, ct);
    }

    // --- IFaultInjectionContext ---

    public IDisposable AddFault(FaultRule fault)
    {
        if (_faultInjector is null)
            throw new InvalidOperationException("Fault injection not enabled");
        return _faultInjector.AddFault(fault);
    }

    public IDisposable AddReceiveFault(FaultRule fault)
    {
        if (_receiveFaultInjector is null)
            throw new InvalidOperationException("Receive fault injection not enabled");
        return _receiveFaultInjector.AddReceiveFault(fault);
    }

    public void TagFault(string faultId, IDisposable handle)
        => _faultHandles[faultId] = handle;

    public void ClearFaults(string? faultId = null)
    {
        if (faultId is null)
        {
            var snapshot = _faultHandles.ToList();
            foreach (var (key, handle) in snapshot)
            {
                if (_faultHandles.TryRemove(key, out _)) handle.Dispose();
            }
        }
        else if (_faultHandles.TryRemove(faultId, out var h))
        {
            h.Dispose();
        }
    }

    public IReadOnlyList<CanFrame> GetRecentFrames() => _recentFrames.Snapshot();

    // IStepVariableStore — 步骤间传值（Phase A）。同 case 内串行执行，无并发写。
    public IDictionary<string, object> Variables { get; } = new Dictionary<string, object>();

    // IAssertionContext.GetRecentDecodedFrames — tracks decoded frames for race-condition-free WaitForFrame
    private readonly List<DecodedFrame> _decodedRecentFrames = new();
    private readonly object _decodedFramesLock = new();

    public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames()
    {
        lock (_decodedFramesLock) return _decodedRecentFrames.ToList();
    }

    public void Dispose()
    {
        // 1. 先取消 consumer loop（阻止处理新帧）
        _consumerCts.Cancel();

        // 2. 再取消 channel 订阅（阻止新帧进入 channel）
        _frameSubscription.Dispose();

        // 3. 等待 consumer 线程退出（确保所有 subscriber 回调已完成）
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

                var key = ToDbcLookupKey(frame.Id.Raw, frame.Id.IsExtended);
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

                // Track decoded frames for GetRecentDecodedFrames (race-condition-free WaitForFrame)
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
                        // Isolate per subscriber; swallow to prevent one bad callback from killing the loop
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
    /// DBC Message.Id stores extended IDs with bit 31 set (e.g., 0x98FEF100).
    /// CanFrame.Id.Raw stores the raw ID without bit 31 (e.g., 0x18FEF100).
    /// </summary>
    private static uint ToDbcLookupKey(uint rawId, bool isExtended) =>
        isExtended ? rawId | 0x80000000u : rawId;
}

/// <summary>
/// IDisposable that removes a subscriber on Dispose.
/// </summary>
internal sealed class SubscriberSubscription : IDisposable
{
    private Action? _dispose;

    public SubscriberSubscription(Action dispose) => _dispose = dispose;

    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
