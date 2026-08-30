using System.Runtime.CompilerServices;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.Replay;

namespace PeakCan.Host.App.Services.J1939;

/// <summary>
/// L3 虚拟帧归并 + DBC ID 三级匹配（spec §9.3 + 修订 9）。
/// 虚拟帧 ID 一律经 <see cref="J1939Id.Compose"/>（spec §16.4，禁止手写位运算）。
/// </summary>
public static class J1939VirtualFrameMerger
{
    private static readonly ConditionalWeakTable<DbcDocument, StrongBox<int>> MatchLevelCache = new();

    /// <summary>
    /// 原始帧 ∪ 完整重组消息的虚拟帧，按 Timestamp 稳定归并（同刻原始帧在前）。
    /// 只有 <see cref="ReassemblyStatus.Complete"/> 的消息产虚拟帧。
    /// </summary>
    public static IReadOnlyList<ReplayFrame> Merge(
        IReadOnlyList<ReplayFrame> raw, IReadOnlyList<ReassembledJ1939Message> messages)
        => raw.Concat(messages.Where(m => m.Status == ReassemblyStatus.Complete).Select(ToVirtualFrame))
              .OrderBy(f => f.Timestamp)
              .ToList();

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

    /// <summary>
    /// 三级回退匹配（首个命中级别即停；命中级别按 DbcDocument 缓存——同一 DBC 的消息 ID 惯例一致，
    /// 未命中不缓存，miss 后续调用重扫）：
    /// ① 精确 29 位（DBC Id 先剥 bit31 IDE 位）；② 掩掉优先级 <c>&amp; 0x03FFFFFF</c>；
    /// ③ PDU1 PF 段 <c>&amp; 0x00FF0000</c>（覆盖 PGN&lt;&lt;8|SA 惯例与 BAM 广播虚拟帧）。
    /// </summary>
    public static Message? FindMessage(DbcDocument? dbc, uint virtualId)
    {
        if (dbc is null)
            return null;

        var box = MatchLevelCache.GetValue(dbc, _ => new StrongBox<int>(0));
        int level = box.Value;
        if (level == 0)
        {
            level = ResolveLevel(dbc, virtualId);
            // 仅缓存命中级别（1/2/3）；未命中（-1）不得缓存——否则首次 miss 永久毒化
            // 该文档的后续匹配（Task 13 review Finding 1；代价：每次 miss 重扫一遍）。
            if (level > 0)
                box.Value = level;
        }

        return level switch
        {
            1 => dbc.Messages.FirstOrDefault(m => (m.Id & J1939Id.Raw29Mask) == virtualId),
            2 => dbc.Messages.FirstOrDefault(m => (m.Id & 0x03FFFFFF) == (virtualId & 0x03FFFFFF)),
            3 => dbc.Messages.FirstOrDefault(m => (m.Id & 0x00FF0000) == (virtualId & 0x00FF0000) && new J1939Id(virtualId).IsPdu1),
            _ => null,
        };
    }

    private static int ResolveLevel(DbcDocument dbc, uint virtualId)
    {
        if (dbc.Messages.Any(m => (m.Id & J1939Id.Raw29Mask) == virtualId))
            return 1;
        if (dbc.Messages.Any(m => (m.Id & 0x03FFFFFF) == (virtualId & 0x03FFFFFF)))
            return 2;
        if (new J1939Id(virtualId).IsPdu1 && dbc.Messages.Any(m => (m.Id & 0x00FF0000) == (virtualId & 0x00FF0000)))
            return 3;
        return -1;
    }
}
