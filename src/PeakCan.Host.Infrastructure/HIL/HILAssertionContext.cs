using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Bridges a virtual CAN channel to the HIL assertion context.
/// Subscribes to channel.FrameReceived, decodes frames via DBC, caches signal values.
/// </summary>
internal sealed class HILAssertionContext : IAssertionContext, IDisposable
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

    public HILAssertionContext(ICanChannel channel, IDbcLookup dbcLookup)
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
        // Sprint 2: delegate to channel (which is a no-op for TraceDrivenChannel)
        return _channel.WriteAsync(frame, ct);
    }

    public void Dispose()
    {
        _frameSubscription.Dispose();

        // Drain remaining frames
        SpinWait.SpinUntil(() => _frameChannel.Reader.Count == 0, 100);

        _consumerCts.Cancel();

        try
        {
            _consumerTask.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected on Cancel
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
                var key = ToDbcLookupKey(frame.Id.Raw, frame.Id.IsExtended);
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
