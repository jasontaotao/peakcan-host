namespace PeakCan.Host.Core.Xcp.Protocol;

/// <summary>
/// XCP 连接/状态命令编码器（CTO 8B，一帧一 CTO，spec §3 Protocol）。
/// 请求帧长度钉死 8B：PID + 参数 + 保留位填零。
/// </summary>
public static class XcpCommandEncoder
{
    /// <summary>
    /// CMD_CONNECT：请求 [FF, mode, reserved×6]。
    /// mode 0x00 = normal mode（S2 唯一合法值）。
    /// </summary>
    public static XcpCtoFrame Connect(byte mode = 0x00)
    {
        return new XcpCtoFrame(new byte[]
        {
            XcpPid.Connect, mode, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        });
    }

    /// <summary>CMD_DISCONNECT：请求 [FE, reserved×7]。</summary>
    public static XcpCtoFrame Disconnect()
    {
        return new XcpCtoFrame(new byte[]
        {
            XcpPid.Disconnect, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        });
    }

    /// <summary>CMD_GET_STATUS：请求 [FD, reserved×7]。</summary>
    public static XcpCtoFrame GetStatus()
    {
        return new XcpCtoFrame(new byte[]
        {
            XcpPid.GetStatus, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        });
    }

    /// <summary>CMD_SYNCH：请求 [FC, reserved×7]。</summary>
    public static XcpCtoFrame Synch()
    {
        return new XcpCtoFrame(new byte[]
        {
            XcpPid.Synch, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        });
    }

    /// <summary>CMD_GET_COMM_MODE_INFO：请求 [FB, reserved×7]。</summary>
    public static XcpCtoFrame GetCommModeInfo()
    {
        return new XcpCtoFrame(new byte[]
        {
            XcpPid.GetCommModeInfo, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        });
    }

    /// <summary>
    /// CMD_SET_MTA：请求 [F6, reserved×2, addrExt, addr(4B LE)]。
    /// 与 SET_DAQ_PTR 同构（spec §1：Intel 字节序）。
    /// </summary>
    public static XcpCtoFrame SetMta(byte addressExtension, uint address)
    {
        return new XcpCtoFrame(new byte[]
        {
            XcpPid.SetMta, 0x00, 0x00, addressExtension,
            (byte)(address & 0xFF), (byte)((address >> 8) & 0xFF),
            (byte)((address >> 16) & 0xFF), (byte)((address >> 24) & 0xFF),
        });
    }

    /// <summary>
    /// CMD_UPLOAD：请求 [F5, blockMode=00, reserved, nbytes, reserved×4]。
    /// 响应 = [FF, data×nbytes]；nbytes ≤ 7（CTO 8B − PID 1B）。
    /// </summary>
    public static XcpCtoFrame Upload(byte numberOfBytes)
    {
        if (numberOfBytes is < 1 or > XcpCtoFrame.MaxByteLength - 1)
            throw new ArgumentException($"UPLOAD byte count must be 1..{XcpCtoFrame.MaxByteLength - 1} (CTO 8B − PID), got {numberOfBytes}.", nameof(numberOfBytes));

        return new XcpCtoFrame(new byte[]
        {
            XcpPid.Upload, 0x00, 0x00, numberOfBytes, 0x00, 0x00, 0x00, 0x00,
        });
    }

    /// <summary>
    /// CMD_SHORT_UPLOAD：请求 [F4, reserved×2, nbytes, addr(4B LE)]。
    /// CTO 8B 无 ADDR_EXT 字段（与 SET_MTA 不同：nbytes 挤掉了 ADDR_EXT 槽位）。
    /// 响应 = [FF, data×nbytes]；nbytes ≤ 7。
    /// </summary>
    public static XcpCtoFrame ShortUpload(byte numberOfBytes, uint address)
    {
        if (numberOfBytes is < 1 or > XcpCtoFrame.MaxByteLength - 1)
            throw new ArgumentException($"SHORT_UPLOAD byte count must be 1..{XcpCtoFrame.MaxByteLength - 1} (CTO 8B − PID), got {numberOfBytes}.", nameof(numberOfBytes));

        return new XcpCtoFrame(new byte[]
        {
            XcpPid.ShortUpload, 0x00, 0x00, numberOfBytes,
            (byte)(address & 0xFF), (byte)((address >> 8) & 0xFF),
            (byte)((address >> 16) & 0xFF), (byte)((address >> 24) & 0xFF),
        });
    }

    /// <summary>
    /// CMD_DOWNLOAD：请求 [F0, blockMode=00, reserved, nbytes, data…, pad]。
    /// <b>S2 编解码实现、调度禁用</b>（spec 决策 D2 / §0 非目标）——
    /// Planner/Scheduler/Receive 全链路不得产生 DOWNLOAD 流量；本方法仅供协议层测试与 S5 预留。
    /// data ≤ 4B（CTO 8B − 4B header）。
    /// </summary>
    public static XcpCtoFrame Download(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > XcpCtoFrame.MaxByteLength - 4)
            throw new ArgumentException($"DOWNLOAD data must be 1..{XcpCtoFrame.MaxByteLength - 4} bytes (CTO 8B − 4B header), got {data.Length}.", nameof(data));

        var frame = new byte[8];
        frame[0] = XcpPid.Download;
        frame[3] = (byte)data.Length;
        data.CopyTo(frame.AsSpan(4));
        return new XcpCtoFrame(frame);
    }

    /// <summary>CMD_SET_DAQ_PTR：请求 [E2, reserved×2, addrExt, addr(4B LE)]——与 SET_MTA 同构。</summary>
    public static XcpCtoFrame SetDaqPtr(byte addressExtension, uint address)
    {
        return new XcpCtoFrame(new byte[]
        {
            XcpPid.SetDaqPtr, 0x00, 0x00, addressExtension,
            (byte)(address & 0xFF), (byte)((address >> 8) & 0xFF),
            (byte)((address >> 16) & 0xFF), (byte)((address >> 24) & 0xFF),
        });
    }

    /// <summary>CMD_WRITE_DAQ：请求 [E1, bitOffset, entrySize(1..4), addrExt, addr(4B LE)]。entrySize ≤ 4B（spec §1 单条目上限）。</summary>
    public static XcpCtoFrame WriteDaq(byte bitOffset, byte entrySize, byte addressExtension, uint address)
    {
        if (entrySize is < 1 or > 4)
            throw new ArgumentException($"WRITE_DAQ entrySize must be 1..4 (single entry ≤4B, spec §1), got {entrySize}.", nameof(entrySize));

        return new XcpCtoFrame(new byte[]
        {
            XcpPid.WriteDaq, bitOffset, entrySize, addressExtension,
            (byte)(address & 0xFF), (byte)((address >> 8) & 0xFF),
            (byte)((address >> 16) & 0xFF), (byte)((address >> 24) & 0xFF),
        });
    }

    /// <summary>CMD_CLEAR_DAQ_LIST：请求 [E3, mode, reserved×4, daqListNum(2B LE)]。</summary>
    public static XcpCtoFrame ClearDaqList(byte mode, ushort daqListNumber)
    {
        return new XcpCtoFrame(new byte[]
        {
            XcpPid.ClearDaqList, mode, 0x00, 0x00, 0x00, 0x00,
            (byte)(daqListNumber & 0xFF), (byte)(daqListNumber >> 8),
        });
    }

    /// <summary>
    /// CMD_START_STOP_DAQ_LIST：请求 [DE, mode, daqList, reserved×5]。
    /// 仅 mode 0(stop)/1(start) 合法——mode 2(select) + SYNCH 组合路径不使用（spec §1 写死）。
    /// </summary>
    public static XcpCtoFrame StartStopDaqList(byte mode, ushort daqListNumber)
    {
        if (mode > 0x01)
            throw new ArgumentException($"START_STOP_DAQ_LIST mode must be 0 (stop) or 1 (start); mode 2 (select) is not used in S2 (spec §1), got {mode}.", nameof(mode));

        return new XcpCtoFrame(new byte[]
        {
            XcpPid.StartStopDaqList, mode, (byte)daqListNumber, 0x00, 0x00, 0x00, 0x00, 0x00,
        });
    }

    /// <summary>CMD_START_STOP_SYNCH：请求 [DD, reserved×7]。S2 不使用 select 组合路径，仅 DAQ start/stop 同步。</summary>
    public static XcpCtoFrame StartStopSynch()
    {
        return new XcpCtoFrame(new byte[]
        {
            XcpPid.StartStopSynch, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        });
    }

    /// <summary>CMD_GET_DAQ_PROCESSOR_INFO：请求 [D8, reserved×7]。</summary>
    public static XcpCtoFrame GetDaqProcessorInfo()
    {
        return new XcpCtoFrame(new byte[]
        {
            XcpPid.GetDaqProcessorInfo, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        });
    }

    /// <summary>CMD_GET_DAQ_RESOLUTION_INFO：请求 [D7, reserved×7]。</summary>
    public static XcpCtoFrame GetDaqResolutionInfo()
    {
        return new XcpCtoFrame(new byte[]
        {
            XcpPid.GetDaqResolutionInfo, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        });
    }

    /// <summary>CMD_GET_DAQ_LIST_INFO：请求 [D9, reserved×3, daqListNum(2B LE), reserved×2]。</summary>
    public static XcpCtoFrame GetDaqListInfo(ushort daqListNumber)
    {
        return new XcpCtoFrame(new byte[]
        {
            XcpPid.GetDaqListInfo, 0x00, 0x00, 0x00,
            (byte)(daqListNumber & 0xFF), (byte)(daqListNumber >> 8), 0x00, 0x00,
        });
    }

    /// <summary>CMD_GET_DAQ_EVENT_INFO：请求 [DA, reserved×2, eventChannel(2B LE), reserved×3]。</summary>
    public static XcpCtoFrame GetDaqEventInfo(ushort eventChannel)
    {
        return new XcpCtoFrame(new byte[]
        {
            XcpPid.GetDaqEventInfo, 0x00, 0x00,
            (byte)(eventChannel & 0xFF), (byte)(eventChannel >> 8),
            0x00, 0x00, 0x00,
        });
    }
}