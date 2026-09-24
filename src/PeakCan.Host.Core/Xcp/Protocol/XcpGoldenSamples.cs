namespace PeakCan.Host.Core.Xcp.Protocol;

/// <summary>
/// 字节级黄金样本（spec §5 验收 1）：连接/状态命令的线上标准答案。
/// 每条命令配请求帧 + 正响应帧 + 负响应帧；由测试逐字节断言，模拟从机（T6）回放。
/// </summary>
public static class XcpGoldenSamples
{
    // ---- CONNECT (0xFF) ----

    /// <summary>CONNECT 请求 [FF, mode=00, reserved×6]。</summary>
    public static ReadOnlyMemory<byte> ConnectRequest { get; } = new byte[] { 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>CONNECT 正响应（ASAM 标准布局）[FF, RESOURCE=04(DAQ), COMM_MODE_BASIC=01(BYTE_ORDER=Intel、SLAVE_BLOCK_MODE=0——bit6 才是块模式；若 A2L 声明 BLOCK SLAVE 即矛盾，A-5 台架核死), MAX_CTO=08, MAX_DTO=0x0040 LE, PROTOCOL_VERSION=01, TRANSPORT_VERSION=01]。</summary>
    public static ReadOnlyMemory<byte> ConnectPositiveResponse { get; } = new byte[] { 0xFF, 0x04, 0x01, 0x08, 0x40, 0x00, 0x01, 0x01 };

    /// <summary>CONNECT 负响应 [FE, ERR_OUT_OF_RANGE]（mode ≠ 0 时）。</summary>
    public static ReadOnlyMemory<byte> ConnectErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.OutOfRange };

    // ---- DISCONNECT (0xFE) ----

    /// <summary>DISCONNECT 请求 [FE, reserved×7]。</summary>
    public static ReadOnlyMemory<byte> DisconnectRequest { get; } = new byte[] { 0xFE, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>DISCONNECT 正响应 [FF]——单字节无字段。</summary>
    public static ReadOnlyMemory<byte> DisconnectPositiveResponse { get; } = new byte[] { 0xFF };

    /// <summary>DISCONNECT 负响应 [FE, ERR_CMD_UNKNOWN]。</summary>
    public static ReadOnlyMemory<byte> DisconnectErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.CmdUnknown };

    // ---- GET_STATUS (0xFD) ----

    /// <summary>GET_STATUS 请求 [FD, reserved×7]。</summary>
    public static ReadOnlyMemory<byte> GetStatusRequest { get; } = new byte[] { 0xFD, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>GET_STATUS 正响应 [FF, sessionStatus=02(DAQ running), protectionStatus=00, reserved, maxDaq=01, reserved×3]。</summary>
    public static ReadOnlyMemory<byte> GetStatusPositiveResponse { get; } = new byte[] { 0xFF, 0x02, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };

    /// <summary>GET_STATUS 负响应 [FE, ERR_CMD_BUSY]（前一命令仍在处理中）。</summary>
    public static ReadOnlyMemory<byte> GetStatusErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.CmdBusy };

    // ---- SYNCH (0xFC) ----

    /// <summary>SYNCH 请求 [FC, reserved×7]。</summary>
    public static ReadOnlyMemory<byte> SynchRequest { get; } = new byte[] { 0xFC, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>SYNCH 正响应 [FF]——单字节无字段。</summary>
    public static ReadOnlyMemory<byte> SynchPositiveResponse { get; } = new byte[] { 0xFF };

    /// <summary>SYNCH 负响应 [FE, ERR_CMD_UNKNOWN]。</summary>
    public static ReadOnlyMemory<byte> SynchErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.CmdUnknown };

    // ---- GET_COMM_MODE_INFO (0xFB) ----

    /// <summary>GET_COMM_MODE_INFO 请求 [FB, reserved×7]。</summary>
    public static ReadOnlyMemory<byte> GetCommModeInfoRequest { get; } = new byte[] { 0xFB, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>GET_COMM_MODE_INFO 正响应 [FF, reserved, commModeOptional=00(无块模式/无交错), reserved, maxBs=00, minSt=00, queueSize=000A(LSB first)]。</summary>
    public static ReadOnlyMemory<byte> GetCommModeInfoPositiveResponse { get; } = new byte[] { 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x00 };

    /// <summary>GET_COMM_MODE_INFO 负响应 [FE, ERR_CMD_UNKNOWN]。</summary>
    public static ReadOnlyMemory<byte> GetCommModeInfoErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.CmdUnknown };

    // ---- SET_MTA (0xF6) ----

    /// <summary>SET_MTA 请求 [F6, reserved×2, addrExt=00, addr=0x00001000 LE]。</summary>
    public static ReadOnlyMemory<byte> SetMtaRequest { get; } = new byte[] { 0xF6, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00 };

    /// <summary>SET_MTA 正响应 [FF]。</summary>
    public static ReadOnlyMemory<byte> SetMtaPositiveResponse { get; } = new byte[] { 0xFF };

    /// <summary>SET_MTA 负响应 [FE, ERR_OUT_OF_RANGE]。</summary>
    public static ReadOnlyMemory<byte> SetMtaErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.OutOfRange };

    // ---- UPLOAD (0xF5) ----

    /// <summary>UPLOAD 请求 [F5, blockMode=00, reserved, nbytes=04, reserved×4]。</summary>
    public static ReadOnlyMemory<byte> UploadRequest { get; } = new byte[] { 0xF5, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>UPLOAD 正响应 [FF, d0..d3]（4B 数据，最小自然帧长）。</summary>
    public static ReadOnlyMemory<byte> UploadPositiveResponse { get; } = new byte[] { 0xFF, 0x12, 0x34, 0x56, 0x78 };

    /// <summary>UPLOAD 负响应 [FE, ERR_SEQUENCE]（未 SET_MTA 就 UPLOAD）。</summary>
    public static ReadOnlyMemory<byte> UploadErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.Sequence };

    // ---- SHORT_UPLOAD (0xF4) ----

    /// <summary>SHORT_UPLOAD 请求 [F4, reserved×2, nbytes=02, addrExt=00, addr=0x00002000 低 3B LE]——addrExt 必须存在（A2L ADDRESS_EXTENSION_FREE=0）。</summary>
    public static ReadOnlyMemory<byte> ShortUploadRequest { get; } = new byte[] { 0xF4, 0x00, 0x00, 0x02, 0x00, 0x00, 0x20, 0x00 };

    /// <summary>SHORT_UPLOAD 正响应 [FF, d0, d1]（2B 数据）。</summary>
    public static ReadOnlyMemory<byte> ShortUploadPositiveResponse { get; } = new byte[] { 0xFF, 0xAB, 0xCD };

    /// <summary>SHORT_UPLOAD 负响应 [FE, ERR_OUT_OF_RANGE]（nbytes 超限）。</summary>
    public static ReadOnlyMemory<byte> ShortUploadErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.OutOfRange };

    // ---- DOWNLOAD (0xF0) —— 编解码实现、调度禁用（spec 决策 D2 / §0 非目标）----

    /// <summary>DOWNLOAD 请求 [F0, blockMode=00, reserved, nbytes=02, data AB CD, pad×2]。</summary>
    public static ReadOnlyMemory<byte> DownloadRequest { get; } = new byte[] { 0xF0, 0x00, 0x00, 0x02, 0xAB, 0xCD, 0x00, 0x00 };

    /// <summary>DOWNLOAD 正响应 [FF]。</summary>
    public static ReadOnlyMemory<byte> DownloadPositiveResponse { get; } = new byte[] { 0xFF };

    /// <summary>DOWNLOAD 负响应 [FE, ERR_WRITE_PROTECTED]。</summary>
    public static ReadOnlyMemory<byte> DownloadErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.WriteProtected };

    // ---- SET_DAQ_PTR (0xE2) ----

    /// <summary>SET_DAQ_PTR 请求 [E2, reserved×2, addrExt=00, addr=0x00000000 LE]——与 SET_MTA 同构。</summary>
    public static ReadOnlyMemory<byte> SetDaqPtrRequest { get; } = new byte[] { 0xE2, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>SET_DAQ_PTR 正响应 [FF]。</summary>
    public static ReadOnlyMemory<byte> SetDaqPtrPositiveResponse { get; } = new byte[] { 0xFF };

    /// <summary>SET_DAQ_PTR 负响应 [FE, ERR_OUT_OF_RANGE]。</summary>
    public static ReadOnlyMemory<byte> SetDaqPtrErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.OutOfRange };

    // ---- WRITE_DAQ (0xE1) ----

    /// <summary>WRITE_DAQ 请求 [E1, bitOffset=00, entrySize=02, addrExt=00, addr=0x00001000 LE]。</summary>
    public static ReadOnlyMemory<byte> WriteDaqRequest { get; } = new byte[] { 0xE1, 0x00, 0x02, 0x00, 0x00, 0x10, 0x00, 0x00 };

    /// <summary>WRITE_DAQ 正响应 [FF]。</summary>
    public static ReadOnlyMemory<byte> WriteDaqPositiveResponse { get; } = new byte[] { 0xFF };

    /// <summary>WRITE_DAQ 负响应 [FE, ERR_DAQ_ACTIVE]（表运行中拒绝重写）。</summary>
    public static ReadOnlyMemory<byte> WriteDaqErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.DaqActive };

    // ---- CLEAR_DAQ_LIST (0xE3) ----

    /// <summary>CLEAR_DAQ_LIST 请求 [E3, mode=00, reserved×4, daqListNum=0000 LE]。</summary>
    public static ReadOnlyMemory<byte> ClearDaqListRequest { get; } = new byte[] { 0xE3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>CLEAR_DAQ_LIST 正响应 [FF]。</summary>
    public static ReadOnlyMemory<byte> ClearDaqListPositiveResponse { get; } = new byte[] { 0xFF };

    /// <summary>CLEAR_DAQ_LIST 负响应 [FE, ERR_DAQ_ACTIVE]（表运行中拒绝清除）。</summary>
    public static ReadOnlyMemory<byte> ClearDaqListErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.DaqActive };

    // ---- START_STOP_DAQ_LIST (0xDE) ----

    /// <summary>START_STOP_DAQ_LIST stop 请求 [DE, mode=00, daqList=00, reserved×5]。</summary>
    public static ReadOnlyMemory<byte> StartStopDaqListStopRequest { get; } = new byte[] { 0xDE, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>START_STOP_DAQ_LIST start 请求 [DE, mode=01, daqList=00, reserved×5]。</summary>
    public static ReadOnlyMemory<byte> StartStopDaqListStartRequest { get; } = new byte[] { 0xDE, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>START_STOP_DAQ_LIST 正响应 [FF, firstPid(1B)=00, reserved×6]。</summary>
    public static ReadOnlyMemory<byte> StartStopDaqListPositiveResponse { get; } = new byte[] { 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>START_STOP_DAQ_LIST 负响应 [FE, ERR_OUT_OF_RANGE]。</summary>
    public static ReadOnlyMemory<byte> StartStopDaqListErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.OutOfRange };

    // ---- START_STOP_SYNCH (0xDD) ----

    /// <summary>START_STOP_SYNCH 请求 [DD, reserved×7]。</summary>
    public static ReadOnlyMemory<byte> StartStopSynchRequest { get; } = new byte[] { 0xDD, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>START_STOP_SYNCH 正响应 [FF]。</summary>
    public static ReadOnlyMemory<byte> StartStopSynchPositiveResponse { get; } = new byte[] { 0xFF };

    /// <summary>START_STOP_SYNCH 负响应 [FE, ERR_CMD_BUSY]。</summary>
    public static ReadOnlyMemory<byte> StartStopSynchErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.CmdBusy };

    // ---- GET_DAQ_PROCESSOR_INFO (0xD8) ----

    /// <summary>GET_DAQ_PROCESSOR_INFO 请求 [D8, reserved×7]。</summary>
    public static ReadOnlyMemory<byte> GetDaqProcessorInfoRequest { get; } = new byte[] { 0xD8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>GET_DAQ_PROCESSOR_INFO 正响应 [FF, maxDaq=0001 LE, maxEventCh=0001 LE, minDaq=00, daqKeyByte=00, reserved]。</summary>
    public static ReadOnlyMemory<byte> GetDaqProcessorInfoPositiveResponse { get; } = new byte[] { 0xFF, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>GET_DAQ_PROCESSOR_INFO 负响应 [FE, ERR_CMD_UNKNOWN]。</summary>
    public static ReadOnlyMemory<byte> GetDaqProcessorInfoErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.CmdUnknown };

    // ---- GET_DAQ_RESOLUTION_INFO (0xD7) ----

    /// <summary>GET_DAQ_RESOLUTION_INFO 请求 [D7, reserved×7]。</summary>
    public static ReadOnlyMemory<byte> GetDaqResolutionInfoRequest { get; } = new byte[] { 0xD7, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>
    /// GET_DAQ_RESOLUTION_INFO 正响应 [FF, granularityDaq=01, maxOdtEntrySizeDaq=04,
/// granularityStim=00, maxOdtEntrySizeStim=00, timestampTicks(1B)=00, reserved×2]。
    /// byte[2] = MAX_ODT_ENTRY_SIZE_DAQ（XCP 1.0 GET_DAQ_RESOLUTION_INFO 表）——
    /// 0x04 对齐 spec §1（单条目 ≤4B）与 App_merge_INCA.a2l 声明 0x04。</summary>
    public static ReadOnlyMemory<byte> GetDaqResolutionInfoPositiveResponse { get; } = new byte[] { 0xFF, 0x01, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>GET_DAQ_RESOLUTION_INFO 负响应 [FE, ERR_CMD_UNKNOWN]。</summary>
    public static ReadOnlyMemory<byte> GetDaqResolutionInfoErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.CmdUnknown };

    // ---- GET_DAQ_LIST_INFO (0xD9) ----

    /// <summary>GET_DAQ_LIST_INFO 请求 [D9, reserved×3, daqListNum=0000 LE, reserved×2]。</summary>
    public static ReadOnlyMemory<byte> GetDaqListInfoRequest { get; } = new byte[] { 0xD9, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>GET_DAQ_LIST_INFO 正响应 [FF, mode=00, maxOdt=0F, maxDaqList=01, firstPid=0000 LE, reserved×2]。</summary>
    public static ReadOnlyMemory<byte> GetDaqListInfoPositiveResponse { get; } = new byte[] { 0xFF, 0x00, 0x0F, 0x01, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>GET_DAQ_LIST_INFO 负响应 [FE, ERR_OUT_OF_RANGE]。</summary>
    public static ReadOnlyMemory<byte> GetDaqListInfoErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.OutOfRange };

    // ---- GET_DAQ_EVENT_INFO (0xDA) ----

    /// <summary>GET_DAQ_EVENT_INFO 请求 [DA, reserved×2, eventChannel=0000 LE, reserved×3]。</summary>
    public static ReadOnlyMemory<byte> GetDaqEventInfoRequest { get; } = new byte[] { 0xDA, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>
    /// GET_DAQ_EVENT_INFO 正响应 [FF, eventChInfo=40, maxDaqList=0F, eventChannel=0000 LE,
    /// eventCycle(1B)=0A, timeUnit=06, priority=00]。
    /// (0x0A, 0x06)：XCP 线上 TIME_UNIT 表 0=1ns 起、6=1ms → 10×1ms = 10ms = 100Hz（spec §1）。
    /// 数值恰与 A2L 声明 TIME_CYCLE=0x0A/TIME_UNIT=0x06 相同，但两套编号体系不是一回事
    /// （线上 0=1ns；A2L 是 A2ML UNIT_1MS=6）——一致纯属模拟从机按 spec §1 的设定。
    /// 旧样本 (01,00)=1×1ns 曾被误注为"100Hz"（T7 评审修正）。</summary>
    public static ReadOnlyMemory<byte> GetDaqEventInfoPositiveResponse { get; } = new byte[] { 0xFF, 0x40, 0x0F, 0x00, 0x00, 0x0A, 0x06, 0x00 };
    /// <summary>GET_DAQ_EVENT_INFO 正响应（TIME_UNIT≠0）：[FF, eventChInfo=40, maxDaqList=0F, eventChannel=0000 LE, eventCycle=05, timeUnit=02, priority=01]——钉死三个单字节不再折叠。</summary>
    public static ReadOnlyMemory<byte> GetDaqEventInfoTimeUnitNonZeroPositiveResponse { get; } = new byte[] { 0xFF, 0x40, 0x0F, 0x00, 0x00, 0x05, 0x02, 0x01 };


    /// <summary>GET_DAQ_EVENT_INFO 负响应 [FE, ERR_OUT_OF_RANGE]。</summary>
    public static ReadOnlyMemory<byte> GetDaqEventInfoErrorResponse { get; } = new byte[] { 0xFE, (byte)XcpError.OutOfRange };
}
