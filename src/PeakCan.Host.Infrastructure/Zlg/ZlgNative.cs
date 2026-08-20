using System.Runtime.InteropServices;

namespace PeakCan.Host.Infrastructure.Zlg;

// ZLG zlgcan.dll P/Invoke declarations — ZCAN API (new API, not VCI).
// Device type constants.

/// <summary>ZLG device type constants for ZCAN_OpenDevice.</summary>
public static class ZlgDeviceType
{
    public const uint USBCAN1 = 1;
    public const uint USBCAN2 = 4;
    public const uint USBCANFD = 21;
    public const uint USBCANFD_200U = 70;
}

/// <summary>INIT_CONFIG.Filter 过滤模式。</summary>
public static class ZlgFilterMode
{
    public const byte ReceiveAll = 0;
    public const byte SingleFilter = 1;
    public const byte DualFilter = 2;
}

/// <summary>INIT_CONFIG.Mode 工作模式。</summary>
public static class ZlgWorkMode
{
    public const byte Normal = 0;
    public const byte ListenOnly = 1;
    public const byte Loopback = 2;
}

/// <summary>CAN 经典帧（8 字节数据）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ZlgCanMsg
{
    public uint ID;
    public uint TimeStamp;
    public byte TimeFlag;
    public byte SendType;
    public byte RemoteFlag;
    public byte ExternFlag;
    public byte DataLen;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public byte[] Data;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public byte[] Reserved;
}

/// <summary>CAN FD 帧（64 字节数据）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ZlgCanFdMsg
{
    public uint ID;
    public uint TimeStamp;
    public byte TimeFlag;
    public byte SendType;
    public byte RemoteFlag;
    public byte ExternFlag;
    public byte DataLen;
    public byte Reserved0;
    public byte Reserved1;
    public byte Reserved2;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] Data;
}

/// <summary>CAN 初始化配置。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZlgInitConfig
{
    public uint AccCode;
    public uint AccMask;
    public uint Reserved;
    public byte Filter;
    public byte Timing0;
    public byte Timing1;
    public byte Mode;
}

/// <summary>设备信息。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZlgDeviceInfo
{
    public ushort hwVersion;
    public ushort fwVersion;
    public ushort drVersion;
    public ushort inVersion;
    public ushort irqNum;
    public byte canNum;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
    public byte[] strSerialNum;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)]
    public byte[] strDeviceType;
}

/// <summary>CAN FD 数据段波特率参考值（用于 ZCAN_SetReference）。</summary>
public static class ZlgFdDataRate
{
    public const uint Rate1Mbps = 0x00000000;
    public const uint Rate2Mbps = 0x00000001;
    public const uint Rate5Mbps = 0x00000002;
}

/// <summary>ZCAN_SetReference 参考代码。</summary>
public static class ZlgReferenceCode
{
    public const uint CanFdDataRate = 0x000C;
    public const uint CanFdDataTiming = 0x000D;
}

/// <summary>
/// 原生 zlgcan.dll API — ZCAN 接口（新版本 DLL）。
/// 所有函数返回 1=成功，0=失败。
/// </summary>
public static class ZlgNative
{
    private const string Dll = "zlgcan.dll";

    [DllImport(Dll)]
    public static extern uint ZCAN_OpenDevice(uint deviceType, uint deviceIndex, uint reserved);

    [DllImport(Dll)]
    public static extern uint ZCAN_CloseDevice(uint deviceType, uint deviceIndex);

    [DllImport(Dll)]
    public static extern uint ZCAN_InitCAN(uint deviceType, uint deviceIndex, uint canIndex, ref ZlgInitConfig config);

    [DllImport(Dll)]
    public static extern uint ZCAN_StartCAN(uint deviceType, uint deviceIndex, uint canIndex);

    [DllImport(Dll)]
    public static extern uint ZCAN_ResetCAN(uint deviceType, uint deviceIndex, uint canIndex);

    [DllImport(Dll)]
    public static extern uint ZCAN_ClearBuffer(uint deviceType, uint deviceIndex, uint canIndex);

    [DllImport(Dll)]
    public static extern uint ZCAN_GetDeviceInf(uint deviceType, uint deviceIndex, out ZlgDeviceInfo info);

    [DllImport(Dll)]
    public static extern uint ZCAN_GetReceiveNum(uint deviceType, uint deviceIndex, uint canIndex);

    [DllImport(Dll)]
    public static extern uint ZCAN_Transmit(uint deviceType, uint deviceIndex, uint canIndex, ref ZlgCanMsg msg, uint len);

    [DllImport(Dll)]
    public static extern uint ZCAN_Receive(uint deviceType, uint deviceIndex, uint canIndex, [In, Out] ZlgCanMsg[] msg, uint len, int waitTime);

    [DllImport(Dll)]
    public static extern uint ZCAN_TransmitFD(uint deviceType, uint deviceIndex, uint canIndex, ref ZlgCanFdMsg msg, uint len);

    [DllImport(Dll)]
    public static extern uint ZCAN_ReceiveFD(uint deviceType, uint deviceIndex, uint canIndex, [In, Out] ZlgCanFdMsg[] msg, uint len, int waitTime);

    [DllImport(Dll)]
    public static extern uint ZCAN_SetReference(uint deviceType, uint deviceIndex, uint canIndex, uint refCode, IntPtr data);
}