namespace PeakCan.Host.Core.Xcp.Protocol;

/// <summary>
/// XCP 连接/状态命令响应解码器（spec §3 Protocol）。
/// 输入为完整响应帧字节（含 PID），各方法校验 PID 并提取字段。
/// 正响应 PID 0xFF / 错误 PID 0xFE——三流共用 CAN_ID_SLAVE，按首字节分流（spec §3 Receive）。
/// </summary>
public static class XcpResponseDecoder
{
    /// <summary>
    /// CMD_CONNECT 正响应：[FF, protocolVersion, transportVersion, resources, commModeBasic, reserved×3]。
    /// </summary>
    public static XcpConnectResponse Connect(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 5);

        return new XcpConnectResponse(
            ProtocolVersion: response[1],
            TransportVersion: response[2],
            Resources: response[3],
            CommModeBasic: response[4]);
    }

    /// <summary>CMD_DISCONNECT 正响应：[FF]，无字段。</summary>
    public static void Disconnect(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 1);
    }

    /// <summary>
    /// CMD_GET_STATUS 正响应：[FF, sessionStatus, protectionStatus, reserved, maxDaq, reserved×3]。
    /// </summary>
    public static XcpGetStatusResponse GetStatus(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 5);

        return new XcpGetStatusResponse(
            SessionStatus: response[1],
            ProtectionStatus: response[2],
            MaxDaq: response[4]);
    }

    /// <summary>CMD_SYNCH 正响应：[FF]，无字段。</summary>
    public static void Synch(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 1);
    }

    /// <summary>
    /// CMD_GET_COMM_MODE_INFO 正响应：[FF, reserved, commModeOptional, reserved, maxBs, minSt, queueSize(LSB,MSB)]。
    /// </summary>
    public static XcpGetCommModeInfoResponse GetCommModeInfo(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 8);

        return new XcpGetCommModeInfoResponse(
            CommModeOptional: response[2],
            MaxBs: response[4],
            MinSt: response[5],
            QueueSize: (ushort)(response[6] | (response[7] << 8)));
    }

    /// <summary>
    /// 负响应：[FE, ERR code]。未知错误码抛 ArgumentOutOfRangeException（不静默映射为成功）。
    /// </summary>
    public static XcpErrorResponse Error(ReadOnlySpan<byte> response)
    {
        if (response.Length < 2)
            throw new ArgumentException($"Error response must be at least 2 bytes (PID + code), got {response.Length}.", nameof(response));

        if (response[0] != XcpPid.Error)
            throw new ArgumentException($"Expected error PID 0x{XcpPid.Error:X2}, got 0x{response[0]:X2}.", nameof(response));

        if (!Enum.IsDefined(typeof(XcpError), response[1]))
            throw new ArgumentOutOfRangeException(nameof(response), $"Unknown XCP error code 0x{response[1]:X2} — refusing to map to a known error.");

        return new XcpErrorResponse((XcpError)response[1]);
    }

    /// <summary>CMD_SET_MTA 正响应：[FF]，无字段。</summary>
    public static void SetMta(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 1);
    }

    /// <summary>CMD_UPLOAD 正响应：[FF, data…]。返回 data 切片（不含 PID）。</summary>
    public static byte[] Upload(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 2);
        return response[1..].ToArray();
    }

    /// <summary>CMD_SHORT_UPLOAD 正响应：[FF, data…]。返回 data 切片（不含 PID）。</summary>
    public static byte[] ShortUpload(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 2);
        return response[1..].ToArray();
    }

    /// <summary>
    /// CMD_DOWNLOAD 正响应：[FF]，无字段。
    /// <b>S2 编解码实现、调度禁用</b>（spec 决策 D2）——仅供协议层测试与 S5 预留。
    /// </summary>
    public static void Download(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 1);
    }

    /// <summary>CMD_SET_DAQ_PTR 正响应：[FF]，无字段。</summary>
    public static void SetDaqPtr(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 1);
    }

    /// <summary>CMD_WRITE_DAQ 正响应：[FF]，无字段。</summary>
    public static void WriteDaq(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 1);
    }

    /// <summary>CMD_CLEAR_DAQ_LIST 正响应：[FF]，无字段。</summary>
    public static void ClearDaqList(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 1);
    }
    /// <summary>CMD_START_STOP_DAQ_LIST 正响应：[FF, firstPid(1B), reserved×6]。</summary>

    public static XcpStartStopDaqListResponse StartStopDaqList(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 2);

        return new XcpStartStopDaqListResponse(
            FirstPid: response[1]);
    }

    /// <summary>CMD_START_STOP_SYNCH 正响应：[FF]，无字段。</summary>
    public static void StartStopSynch(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 1);
    }

    /// <summary>CMD_GET_DAQ_PROCESSOR_INFO 正响应：[FF, maxDaq(2B LE), maxEventChannel(2B LE), minDaq, daqKeyByte, reserved]。</summary>
    public static XcpGetDaqProcessorInfoResponse GetDaqProcessorInfo(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 7);

        return new XcpGetDaqProcessorInfoResponse(
            MaxDaq: (ushort)(response[1] | (response[2] << 8)),
            MaxEventChannel: (ushort)(response[3] | (response[4] << 8)),
            MinDaq: response[5],
            DaqKeyByte: response[6]);
    }

    /// <summary>CMD_GET_DAQ_RESOLUTION_INFO 正响应：[FF, granularityDaq, maxIdentifierDaq, granularityStim, maxIdentifierStim, timestampTicks(1B), reserved×2]。</summary>
    public static XcpGetDaqResolutionInfoResponse GetDaqResolutionInfo(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 8);

        return new XcpGetDaqResolutionInfoResponse(
            GranularityDaq: response[1],
            MaxIdentifierDaq: response[2],
            GranularityStim: response[3],
            MaxIdentifierStim: response[4],
            TimestampTicks: response[5]);
    }

    /// <summary>CMD_GET_DAQ_LIST_INFO 正响应：[FF, mode, maxOdt, maxDaqList, firstPid(2B LE), reserved×2]。</summary>
    public static XcpGetDaqListInfoResponse GetDaqListInfo(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 6);

        return new XcpGetDaqListInfoResponse(
            Mode: response[1],
            MaxOdt: response[2],
            MaxDaqList: response[3],
            FirstPid: (ushort)(response[4] | (response[5] << 8)));
    }

    /// <summary>CMD_GET_DAQ_EVENT_INFO 正响应：[FF, eventChInfo, maxDaqList, eventChannel(2B LE), eventCycle(1B), eventChannelTimeUnit(1B), priority(1B)]。</summary>
    public static XcpGetDaqEventInfoResponse GetDaqEventInfo(ReadOnlySpan<byte> response)
    {
        ValidatePositiveResponse(response, minLength: 8);

        return new XcpGetDaqEventInfoResponse(
            EventChInfo: response[1],
            MaxDaqList: response[2],
            EventChannel: (ushort)(response[3] | (response[4] << 8)),
            EventCycle: response[5],
            EventChannelTimeUnit: response[6],
            Priority: response[7]);
    }
    private static void ValidatePositiveResponse(ReadOnlySpan<byte> response, int minLength)
    {
        if (response.Length < minLength)
            throw new ArgumentException($"Response too short: expected ≥{minLength} bytes, got {response.Length}.", nameof(response));

        if (response[0] != XcpPid.PositiveResponse)
            throw new ArgumentException($"Expected positive response PID 0x{XcpPid.PositiveResponse:X2}, got 0x{response[0]:X2}.", nameof(response));
    }

/// <summary>CMD_START_STOP_DAQ_LIST 正响应字段。</summary>
public readonly record struct XcpStartStopDaqListResponse(byte FirstPid);

/// <summary>CMD_GET_DAQ_PROCESSOR_INFO 正响应字段。</summary>
public readonly record struct XcpGetDaqProcessorInfoResponse(
    ushort MaxDaq,
    ushort MaxEventChannel,
    byte MinDaq,
    byte DaqKeyByte);

/// <summary>CMD_GET_DAQ_RESOLUTION_INFO 正响应字段。</summary>
public readonly record struct XcpGetDaqResolutionInfoResponse(
    byte GranularityDaq,
    byte MaxIdentifierDaq,
    byte GranularityStim,
    byte MaxIdentifierStim,
    byte TimestampTicks);

/// <summary>CMD_GET_DAQ_LIST_INFO 正响应字段。</summary>
public readonly record struct XcpGetDaqListInfoResponse(
    byte Mode,
    byte MaxOdt,
    byte MaxDaqList,
    ushort FirstPid);

/// <summary>CMD_GET_DAQ_EVENT_INFO 正响应字段。</summary>
public readonly record struct XcpGetDaqEventInfoResponse(
    byte EventChInfo,
    byte MaxDaqList,
    ushort EventChannel,
    byte EventCycle,
    byte EventChannelTimeUnit,
    byte Priority);
}

/// <summary>CMD_CONNECT 正响应字段。</summary>
public readonly record struct XcpConnectResponse(
    byte ProtocolVersion,
    byte TransportVersion,
    byte Resources,
    byte CommModeBasic);

/// <summary>CMD_GET_STATUS 正响应字段。</summary>
public readonly record struct XcpGetStatusResponse(
    byte SessionStatus,
    byte ProtectionStatus,
    byte MaxDaq);

/// <summary>CMD_GET_COMM_MODE_INFO 正响应字段。</summary>
public readonly record struct XcpGetCommModeInfoResponse(
    byte CommModeOptional,
    byte MaxBs,
    byte MinSt,
    ushort QueueSize);

/// <summary>负响应字段（ERR code）。</summary>
public readonly record struct XcpErrorResponse(XcpError Code);

