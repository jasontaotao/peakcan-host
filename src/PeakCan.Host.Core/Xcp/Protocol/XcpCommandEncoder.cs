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
}
