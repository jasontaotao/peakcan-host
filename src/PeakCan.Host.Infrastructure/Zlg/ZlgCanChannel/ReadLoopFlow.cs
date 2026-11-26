using PeakCan.HIL.Core;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

// 读循环：两个队列（经典 / FD）都必须排空。
// CAN FD 通道上经典帧与 FD 帧走两个独立接收队列，按 _isFd 门控会静默丢帧。
// 先用 GetReceiveNum 判断是否有数据，再以 wait=0 批量读取，空闲时不阻塞。
public sealed partial class ZlgCanChannel
{
    protected override void DrainClassicFrames()
    {
        var channelHandle = CurrentChannelHandle;
        if (channelHandle == IntPtr.Zero) return;

        var available = _api.GetReceiveNum(channelHandle, ZcanReceiveType.Can);
        if (available == 0 || available > MaxPlausibleFrames) return;

        var want = Math.Min(available, (uint)_classicBuffer.Length);
        var read = _api.Receive(channelHandle, _classicBuffer, want, ReadWaitTimeMs);
        for (uint i = 0; i < read && i < (uint)_classicBuffer.Length; i++)
        {
            var msg = _classicBuffer[i];
            var ts = Timestamp.FromMicroseconds(msg.Timestamp);
            FrameReceived?.Invoke(ZlgCanFrameFormatter.DecodeClassic(Id, msg.Frame, ts));
            MarkFrameEmitted();
        }
    }

    protected override void DrainFdFrames()
    {
        var channelHandle = CurrentChannelHandle;
        if (channelHandle == IntPtr.Zero) return;

        // classic-only 通道（或底层不支持 FD 队列）时这里为 0，直接返回，
        // 绝不调用 ReceiveFD，避免骨架把它计为读失败。
        var available = _api.GetReceiveNum(channelHandle, ZcanReceiveType.CanFd);
        if (available == 0 || available > MaxPlausibleFrames) return;

        var want = Math.Min(available, (uint)_fdBuffer.Length);
        var read = _api.ReceiveFd(channelHandle, _fdBuffer, want, ReadWaitTimeMs);
        for (uint i = 0; i < read && i < (uint)_fdBuffer.Length; i++)
        {
            var msg = _fdBuffer[i];
            var ts = Timestamp.FromMicroseconds(msg.Timestamp);
            FrameReceived?.Invoke(ZlgCanFrameFormatter.DecodeFd(Id, msg.Frame, ts));
            MarkFrameEmitted();
        }
    }

    /// <summary>
    /// give-up 收口：对齐 PEAK 通道——标记断开 + best-effort 复位 + 释放设备引用。
    /// </summary>
    protected override void OnReadLoopGiveUp()
        => MarkDisconnectedAfterReadLoopGiveUp();
}
