using PeakCan.HIL.Core;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

// 读循环：P2-1 2026-09-06 骨架化重写。
// 轮询调度 / 失败计数 / backoff / give-up 判定 / ReadLoopError 每订阅者隔离
// 收敛到 ChannelReadLoop 骨架（含骨架统一持有的带属性 LoggerMessage——
// 日志改为按 ChannelId.Handle 辨识设备，dev/can 索引编码于 handle 高位）。
// 本文件只剩 3 个厂商 hook。
public sealed partial class ZlgCanChannel
{
    /// <inheritdoc cref="ChannelReadLoop.DrainClassicFrames"/>
    protected override void DrainClassicFrames()
    {
        while (true)
        {
            var ret = _reader.ReadClassic(_devType, _devIdx, _canIdx, out var msg);
            if (ret == 0) break;
            var ts = Timestamp.FromMillis(msg.TimeStamp, 0);
            FrameReceived?.Invoke(ZlgCanFrameFormatter.DecodeClassic(Id, msg, ts));
            MarkFrameEmitted();
        }
    }

    /// <inheritdoc cref="ChannelReadLoop.DrainFdFrames"/>
    protected override void DrainFdFrames()
    {
        while (true)
        {
            var ret = _reader.ReadFd(_devType, _devIdx, _canIdx, out var fdMsg);
            if (ret == 0) break;
            // ZLG 的时间戳单位是毫秒
            var ts = Timestamp.FromMillis(fdMsg.TimeStamp, 0);
            FrameReceived?.Invoke(ZlgCanFrameFormatter.DecodeFd(Id, fdMsg, ts));
            MarkFrameEmitted();
        }
    }

    /// <summary>
    /// give-up 收口：对齐 PEAK 通道（commit c9fbaaa）——标记断开（UI 不再显示
    /// "已连接但总线已死"）+ best-effort 复位 + 释放设备引用。
    /// </summary>
    protected override void OnReadLoopGiveUp()
        => MarkDisconnectedAfterReadLoopGiveUp();
}
