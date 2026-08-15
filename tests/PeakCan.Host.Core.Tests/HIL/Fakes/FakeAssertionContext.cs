using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.Tests.HIL.Fakes;

/// <summary>
/// Hand-rolled fake IAssertionContext for unit testing.
/// Supports pushing decoded frames to trigger callbacks.
/// </summary>
internal sealed class FakeAssertionContext : IAssertionContext, IHasFrameSink
{
    private readonly List<Action<DecodedFrame>> _subscribers = new();
    private readonly Dictionary<string, double> _signalValues = new();
    private readonly List<CanFrame> _sentFrames = new();

    public IReadOnlyList<CanFrame> SentFrames => _sentFrames;
    public double CurrentTimestamp { get; set; }
    public System.Collections.Generic.IReadOnlyList<PeakCan.HIL.Core.HIL.Contracts.DecodedFrame> GetRecentDecodedFrames() => Array.Empty<PeakCan.HIL.Core.HIL.Contracts.DecodedFrame>();

    // IHasFrameSink: 记录挂载/排空调用，供 sink 生命周期测试断言
    public IHilFrameSink? ActiveSink { get; private set; }
    public int DrainCalls { get; private set; }
    public void SetFrameSink(IHilFrameSink? sink) => ActiveSink = sink;
    public Task WaitForFrameDrainAsync(CancellationToken ct = default)
    {
        DrainCalls++;
        return Task.CompletedTask;
    }

    public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame)
    {
        _subscribers.Add(onFrame);
        return new FakeSubscription(() => _subscribers.Remove(onFrame));
    }

    public double? GetSignalValue(string signalName, int maxAgeMs = 5000) =>
        _signalValues.TryGetValue(signalName, out var v) ? v : null;

    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct)
    {
        _sentFrames.Add(frame);
        return ValueTask.FromResult(Result<Unit>.Ok(default));
    }

    /// <summary>
    /// Set a signal value (simulates decode cache update).
    /// </summary>
    public void SetSignal(string name, double value) => _signalValues[name] = value;

    /// <summary>
    /// Push a decoded frame to all subscribers.
    /// </summary>
    public void PushFrame(DecodedFrame frame)
    {
        foreach (var subscriber in _subscribers.ToList())
            subscriber(frame);
    }

    /// <summary>
    /// Push a raw frame with empty signals.
    /// </summary>
    public void PushFrame(CanFrame frame) => PushFrame(new DecodedFrame(frame, new Dictionary<string, double>()));

    private sealed class FakeSubscription : IDisposable
    {
        private Action? _dispose;
        public FakeSubscription(Action dispose) => _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
