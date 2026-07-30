using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Bridges a physical CAN channel (PeakCanChannel) to the HIL assertion context.
/// Reuses the same thread model as HILAssertionContext.
/// The only difference: SendFrameAsync delegates to _channel.WriteAsync (real hardware).
/// </summary>
internal sealed class PeakCanAssertionContext : IAssertionContext, IHasRecentFrames, IDisposable
{
    private readonly ICanChannel _channel;
    private readonly IDbcLookup _dbcLookup;
    private readonly Channel<CanFrame> _frameChannel;
    private readonly CancellationTokenSource _consumerCts = new();
    private readonly Task _consumerTask;
    private readonly ConcurrentDictionary<string, (double Value, double TimestampUs)> _signalCache = new();
    private double _currentTimestamp;
    private readonly IDisposable _frameSubscription;
    private ImmutableList<Action<DecodedFrame>> _subscribers = ImmutableList<Action<DecodedFrame>>.Empty;
    private readonly CircularBuffer<CanFrame> _recentFrames = new(capacity: 50);

    public PeakCanAssertionContext(ICanChannel channel, IDbcLookup dbcLookup)
    {
        _channel = channel;
        _dbcLookup = dbcLookup;
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
        return _channel.WriteAsync(frame, ct);
    }

    public IReadOnlyList<CanFrame> GetRecentFrames() => _recentFrames.Snapshot();

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

                var key = DbcLookupKey.ToLookupKey(frame.Id.Raw, frame.Id.IsExtended);
                var message = _dbcLookup.FindMessage(key);

                DecodedFrame decoded;
                if (message is not null)
                {
                    var signals = new Dictionary<string, double>();
                    foreach (var signal in message.Signals)
                    {
                        var signalName = $"{message.Name}.{signal.Name}";
                        var value = SignalDecoder.Decode(frame.Data.Span, signal);
                        signals[signalName] = value;
                        _signalCache[signalName] = (value, _currentTimestamp);
                    }
                    decoded = new DecodedFrame(frame, signals);
                }
                else
                {
                    decoded = new DecodedFrame(frame, new Dictionary<string, double>());
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
}
