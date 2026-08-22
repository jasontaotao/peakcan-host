using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Thins wrapper over <see cref="SingleChannelContext"/>.
/// Preserves the original class name so <see cref="HeadlessHostBuilder"/> (line 108)
/// compiles without changes. All members delegate to the internal SingleChannelContext.
/// </summary>
internal sealed class PeakCanAssertionContext : IAssertionContext, IHasRecentFrames, IStepVariableStore, IHasFrameSink, IDisposable
{
    private readonly SingleChannelContext _inner;

    public PeakCanAssertionContext(ICanChannel channel, IDbcLookup dbcLookup, ILogger? logger = null)
    {
        _inner = new SingleChannelContext(channel, dbcLookup, logger);
    }

    public double CurrentTimestamp => _inner.CurrentTimestamp;

    public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame)
        => _inner.SubscribeDecodedFrames(onFrame);

    public IDisposable SubscribeDecodedFrames(string? channelName, Action<DecodedFrame> onFrame)
        => _inner.SubscribeDecodedFrames(channelName, onFrame);

    public double? GetSignalValue(string signalName, int maxAgeMs = 5000)
        => _inner.GetSignalValue(signalName, maxAgeMs);

    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct)
        => _inner.SendFrameAsync(frame, ct);

    public ValueTask<Result<Unit>> SendFrameAsync(string? channelName, CanFrame frame, CancellationToken ct)
        => _inner.SendFrameAsync(channelName, frame, ct);

    public IReadOnlyList<CanFrame> GetRecentFrames() => _inner.GetRecentFrames();

    public void SetFrameSink(IHilFrameSink? sink) => _inner.SetFrameSink(sink);

    public void SetFrameSink(string? channelName, IHilFrameSink? sink) => _inner.SetFrameSink(channelName, sink);

    public async Task WaitForFrameDrainAsync(CancellationToken ct = default)
        => await _inner.WaitForFrameDrainAsync(ct).ConfigureAwait(false);

    public IDictionary<string, object> Variables => _inner.Variables;

    public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames()
        => _inner.GetRecentDecodedFrames();

    public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames(string? channelName)
        => _inner.GetRecentDecodedFrames(channelName);

    public void Dispose() => _inner.Dispose();
}