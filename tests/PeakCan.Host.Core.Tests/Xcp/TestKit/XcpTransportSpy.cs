using System.Collections.Concurrent;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;

namespace PeakCan.Host.Core.Tests.Xcp.TestKit;

/// <summary>
/// XcpTransportSpy（S2-T6，测试基础设施）：包一层 IXcpTransport，
/// 记录 master 发出的请求帧（顺序保留）并透传 slave→master 帧与 FramesDropped，
/// 供端到端用例断言"master 实际发了什么"。
/// </summary>
public sealed class XcpTransportSpy : IXcpTransport
{
    private readonly IXcpTransport _inner;
    private readonly ConcurrentQueue<CanFrame> _sent = new();

    public XcpTransportSpy(IXcpTransport inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.FrameReceived += Forward;
    }

    /// <inheritdoc />
    public event Action<CanFrame>? FrameReceived;

    /// <inheritdoc />
    public long FramesDropped => _inner.FramesDropped;

    /// <summary>master 已发出的请求帧快照（发送顺序保留）。</summary>
    public IReadOnlyList<CanFrame> Sent => _sent.ToArray();

    /// <summary>请求帧计数。</summary>
    public int WriteCount => _sent.Count;

    /// <summary>被包裹的内层 transport（T16-review M1：测试取从机引用免反射）。</summary>
    internal IXcpTransport Inner => _inner;

    /// <inheritdoc />
    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        _sent.Enqueue(frame);
        return _inner.WriteAsync(frame, ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _inner.FrameReceived -= Forward;
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    private void Forward(CanFrame frame) => FrameReceived?.Invoke(frame);
}
