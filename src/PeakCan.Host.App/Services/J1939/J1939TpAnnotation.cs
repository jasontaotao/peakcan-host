using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.Replay;

namespace PeakCan.Host.App.Services.J1939;

/// <summary>
/// L1 行内注解（spec §9.1）：对 PF=0xEB/0xEC 的帧产出单行解码文本，纯显示层、不做重组。
/// </summary>
public static class J1939TpAnnotation
{
    /// <summary>
    /// 对单个回放帧产出 J1939-21 TP 行内注解；非扩展帧、非 TP 帧（PF≠0xEB/0xEC）或
    /// 畸形 TP 帧一律返回 <c>null</c>（窄捕获 <see cref="ArgumentException"/>，调用方
    /// 零负担）。L1 为逐帧无状态注解：TP.CM 显示控制字节+总量，TP.DT 仅显示序号
    /// （总包数仅存于 TP.CM，单帧不可得）。Task 12 的 L2 面板复用本方法做摘要。
    /// </summary>
    public static string? Annotate(ReplayFrame frame)
    {
        if (!frame.IsExtended)
            return null;
        try
        {
            var id = new J1939Id(frame.Id);
            switch (id.PduFormat)
            {
                case 0xEC:
                    var cm = TpCmMessage.Decode(frame.Data);
                    return cm.Control switch
                    {
                        TpCmControl.Bam => $"TP.CM BAM PGN=0x{cm.Pgn:X6} len={cm.TotalSize} pkts={cm.TotalPackets}",
                        TpCmControl.Rts => $"TP.CM RTS PGN=0x{cm.Pgn:X6} len={cm.TotalSize} pkts={cm.TotalPackets}",
                        TpCmControl.Cts => $"TP.CM CTS next={cm.NextPacketNumber} grant={cm.MaxPacketsPerCts}",
                        TpCmControl.EomAck => $"TP.CM EOM_ACK len={cm.TotalSize} pkts={cm.TotalPackets}",
                        TpCmControl.ConnAbort => $"TP.CM ABORT reason={cm.AbortReason}",
                        _ => null,
                    };
                case 0xEB:
                    var dt = TpDtMessage.Decode(frame.Data);
                    return $"TP.DT #{dt.SequenceNumber}";
                default:
                    return null;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
