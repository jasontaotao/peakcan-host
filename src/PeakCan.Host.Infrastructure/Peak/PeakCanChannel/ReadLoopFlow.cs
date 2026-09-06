using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Peak;

public sealed partial class PeakCanChannel
{
    // Flow A: ReadLoopFlow — P2-1 2026-09-06 骨架化重写。
    // 轮询调度 / 失败计数 / backoff / give-up 判定 / ReadLoopError 每订阅者隔离
    // 收敛到 ChannelReadLoop 骨架；本文件只剩 3 个厂商 hook。
    // （原 ReadLoopAsync 75 LoC + SafeEmitReadLoopError ~16 LoC 删除。）
    //
    // 注意：原先 main 文件里的 LogReadLoopException / LogReadLoopSubscriberThrew
    // 是丢失 [LoggerMessage] 属性的无实现 partial——调用点被编译器静默移除
    // （PEAK 读循环异常日志自 W18 拆分以来从未真正输出）。骨架持有带属性的
    // 声明，此类日志恢复；本文件不再有 loop 日志声明。

    /// <inheritdoc cref="ChannelReadLoop.DrainClassicFrames"/>
    protected override void DrainClassicFrames()
    {
        while (_reader.ReadClassic(_handle, out var msg, out var ts) == TPCANStatus.PCAN_ERROR_OK)
        {
            EmitClassic(msg, ts);
            MarkFrameEmitted();
        }
    }

    /// <inheritdoc cref="ChannelReadLoop.DrainFdFrames"/>
    protected override void DrainFdFrames()
    {
        while (_reader.ReadFd(_handle, out var fdMsg, out var tsMicroseconds) == TPCANStatus.PCAN_ERROR_OK)
        {
            EmitFd(fdMsg, tsMicroseconds);
            MarkFrameEmitted();
        }
    }

    /// <summary>
    /// give-up 收口：best-effort Uninitialize（未来 ConnectAsync 可干净地重新
    /// Initialize）+ 连接门 MarkFailed（IsConnected → false，UI 不再显示假连接）。
    /// </summary>
    protected override void OnReadLoopGiveUp()
    {
        try { PCANBasic.Uninitialize(_handle); } catch (Exception) { /* best-effort */ }
        _gate.MarkFailed();
    }
}
