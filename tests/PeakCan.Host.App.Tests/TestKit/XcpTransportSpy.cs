using System.Collections.Concurrent;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;

namespace PeakCan.Host.App.Tests.TestKit;

/// <summary>XcpTransportSpy（App.Tests 副本，Core.Tests 同款）：记录 master 发出的请求帧。</summary>
public sealed class XcpTransportSpy : IXcpTransport
{
    private readonly IXcpTransport _inner;
    private readonly ConcurrentQueue<CanFrame> _sent = new();

    public XcpTransportSpy(IXcpTransport inner)
    {
        _inner = inner;
        _inner.FrameReceived += Forward;
    }

    public event Action<CanFrame>? FrameReceived;
    public long FramesDropped => _inner.FramesDropped;
    public IReadOnlyList<CanFrame> Sent => _sent.ToArray();
    public int WriteCount => _sent.Count;

    private void Forward(CanFrame frame) => FrameReceived?.Invoke(frame);

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        _sent.Enqueue(frame);
        return _inner.WriteAsync(frame, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _inner.FrameReceived -= Forward;
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
