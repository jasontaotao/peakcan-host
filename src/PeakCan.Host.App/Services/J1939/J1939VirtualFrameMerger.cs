using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.Replay;

namespace PeakCan.Host.App.Services.J1939;

/// <summary>
/// L3 虚拟帧归并（spec §9.3）。虚拟帧 ID 一律经 <see cref="J1939Id.Compose"/>（spec §16.4，禁止手写位运算）。
/// <para>Task 12 雏形：仅归并；Task 13 补 DBC ID 三级匹配与注入点替换。</para>
/// </summary>
public static class J1939VirtualFrameMerger
{
    /// <summary>
    /// 原始帧 ∪ 完整重组消息的虚拟帧，按 Timestamp 稳定归并（同刻原始帧在前）。
    /// 只有 <see cref="ReassemblyStatus.Complete"/> 的消息产虚拟帧。
    /// </summary>
    public static IReadOnlyList<ReplayFrame> Merge(
        IReadOnlyList<ReplayFrame> raw, IReadOnlyList<ReassembledJ1939Message> messages)
        => raw.Concat(messages.Where(m => m.Status == ReassemblyStatus.Complete).Select(ToVirtualFrame))
              .OrderBy(f => f.Timestamp)
              .ToList();

    /// <summary>重组消息 → 虚拟帧（完成时刻 + Compose 全 ID + 原始载荷，PDU1 目标地址进 PS 位）。</summary>
    private static ReplayFrame ToVirtualFrame(ReassembledJ1939Message m)
    {
        var msg = m.Message;
        var id = J1939Id.IsPdu1Pgn(msg.Pgn)
            ? J1939Id.Compose(msg.Priority, msg.Pgn, msg.Sa, msg.Da)
            : J1939Id.Compose(msg.Priority, msg.Pgn, msg.Sa);
        return new ReplayFrame(
            Timestamp: msg.CompletedTimestampSec,
            Id: id,
            Dlc: (byte)Math.Min(msg.Payload.Length, 0xFF),   // 解码以 Data.Length 为准，Dlc 仅显示
            Data: msg.Payload,
            Flags: FrameFlags.None,
            IsExtended: true);
    }
}
