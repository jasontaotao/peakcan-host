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

    /// <summary>CONNECT 正响应 [FF, protocolVersion=01, transportVersion=01, resources=04(DAQ), commModeBasic=01(Motorola), reserved×3]。</summary>
    public static ReadOnlyMemory<byte> ConnectPositiveResponse { get; } = new byte[] { 0xFF, 0x01, 0x01, 0x04, 0x01, 0x00, 0x00, 0x00 };

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
}
