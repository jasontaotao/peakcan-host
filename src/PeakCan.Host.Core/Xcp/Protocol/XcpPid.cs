namespace PeakCan.Host.Core.Xcp.Protocol;

/// <summary>
/// XCP PID / 命令码常量表（XCP 1.0 Part 2 + S2 spec 命令清单）。
/// 字节值按线上协议钉死，禁止调用侧散写字面量。
/// </summary>
public static class XcpPid
{
    // ---- 响应 PID（三流共用 CAN_ID_SLAVE，按首字节分流，spec §3 Receive）----

    /// <summary>Positive response PID（0xFF）。</summary>
    public const byte PositiveResponse = 0xFF;

    /// <summary>Error response PID（0xFE，后跟 XcpError 码）。</summary>
    public const byte Error = 0xFE;

    /// <summary>DAQ DTO 的起始 PID（0x00 起，ODT 编号）。</summary>
    public const byte DaqDtoFirst = 0x00;

    // ---- 命令码（XCP 1.0 Part 2 Table 14 中的 S2 命令清单）----

    /// <summary>CMD_CONNECT (0xFF)。</summary>
    public const byte Connect = 0xFF;

    /// <summary>CMD_DISCONNECT (0xFE)。</summary>
    public const byte Disconnect = 0xFE;

    /// <summary>CMD_GET_STATUS (0xFD)。</summary>
    public const byte GetStatus = 0xFD;

    /// <summary>CMD_SYNCH (0xFC)。</summary>
    public const byte Synch = 0xFC;

    /// <summary>CMD_GET_COMM_MODE_INFO (0xFB)。</summary>
    public const byte GetCommModeInfo = 0xFB;

    /// <summary>CMD_SET_MTA (0xF6)。</summary>
    public const byte SetMta = 0xF6;

    /// <summary>CMD_UPLOAD (0xF5)。</summary>
    public const byte Upload = 0xF5;

    /// <summary>CMD_SHORT_UPLOAD (0xF4)。</summary>
    public const byte ShortUpload = 0xF4;

    /// <summary>CMD_DOWNLOAD (0xF0)——S2 仅实现编解码，调度层禁用（spec 决策 D2）。</summary>
    public const byte Download = 0xF0;

    /// <summary>CMD_CLEAR_DAQ_LIST (0xE3)。</summary>
    public const byte ClearDaqList = 0xE3;

    /// <summary>CMD_SET_DAQ_PTR (0xE2)。</summary>
    public const byte SetDaqPtr = 0xE2;

    /// <summary>CMD_WRITE_DAQ (0xE1)。</summary>
    public const byte WriteDaq = 0xE1;

    /// <summary>CMD_START_STOP_DAQ_LIST (0xDE)——仅 mode 0/1（spec §1）。</summary>
    public const byte StartStopDaqList = 0xDE;

    /// <summary>CMD_START_STOP_SYNCH (0xDD)——S2 不使用 select 组合路径。</summary>
    public const byte StartStopSynch = 0xDD;

    /// <summary>CMD_GET_DAQ_EVENT_INFO (0xDA)。</summary>
    public const byte GetDaqEventInfo = 0xDA;

    /// <summary>CMD_GET_DAQ_LIST_INFO (0xD9)。</summary>
    public const byte GetDaqListInfo = 0xD9;

    /// <summary>CMD_GET_DAQ_PROCESSOR_INFO (0xD8)。</summary>
    public const byte GetDaqProcessorInfo = 0xD8;

    /// <summary>CMD_GET_DAQ_RESOLUTION_INFO (0xD7)。</summary>
    public const byte GetDaqResolutionInfo = 0xD7;
}
