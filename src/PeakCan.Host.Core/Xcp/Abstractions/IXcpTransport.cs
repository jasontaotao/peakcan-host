using PeakCan.HIL.Core;

namespace PeakCan.Host.Core.Xcp.Abstractions;

/// <summary>
/// XCP 传输抽象（spec §3 Abstractions）。本轮绑定 CAN 帧语义
/// （<see cref="CanFrame"/>）；未来 socketcan 等以新的 IXcpTransport
/// 实现扩展，不在本接口上做后端枚举。
/// </summary>
public interface IXcpTransport : IAsyncDisposable
{
    /// <summary>
    /// Fired for every CAN frame received from the XCP slave. Implementations
    /// must dispatch off the underlying channel read thread and must not
    /// block on subscribers.
    /// </summary>
    event Action<CanFrame>? FrameReceived;

    /// <summary>
    /// Number of receive frames dropped by the transport's bounded internal
    /// queues (e.g. DAQ DTO flood with DropOldest). XcpMaster reads this
    /// counter to attribute command timeouts: a growing delta means local
    /// frame drops (acquisition flood), a zero delta means the slave did not
    /// respond. Response frames must never pass through a drop-capable queue
    /// and therefore never contribute to this counter.
    /// </summary>
    long FramesDropped { get; }

    /// <summary>Transmit one CAN frame to the XCP slave; the underlying channel result is passed through.</summary>
    ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default);
}
