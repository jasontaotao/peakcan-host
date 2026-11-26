using System.Runtime.InteropServices;

namespace PeakCan.Host.Infrastructure.Zlg;

// ZLG ZCAN native ABI (zlgcan.dll), transcribed from the vendor's zlgcan.h /
// canframe.h. Every struct here is a byte-for-byte mirror of the C layout; the
// numbers are pinned by ZlgAbiLayoutTests so a silent padding drift fails CI.

/// <summary>ZLG device type constants accepted by ZCAN_OpenDevice.</summary>
public static class ZlgDeviceType
{
    public const uint USBCANFD_200U = 41;
    public const uint USBCANFD_100U = 42;
    public const uint USBCANFD_MINI = 43;
    public const uint USBCANFD_800U = 59;
    public const uint USBCANFD_400U = 76;

    /// <summary>All USBCANFD series types this adapter probes.</summary>
    public static readonly uint[] All =
    {
        USBCANFD_200U,
        USBCANFD_100U,
        USBCANFD_MINI,
        USBCANFD_800U,
        USBCANFD_400U,
    };
}

/// <summary>ZCAN_CHANNEL_INIT_CONFIG.can_type values.</summary>
public static class ZcanCanType
{
    public const uint Can = 0;
    public const uint CanFd = 1;
}

/// <summary>ZCAN_GetReceiveNum / Receive type selector.</summary>
public static class ZcanReceiveType
{
    public const byte Can = 0;
    public const byte CanFd = 1;
    public const byte AllData = 2;
}

/// <summary>can_frame.can_id flag bits (MAKE_CAN_ID).</summary>
public static class ZcanCanIdFlags
{
    public const uint Extended = 0x8000_0000;
    public const uint Remote = 0x4000_0000;
    public const uint Error = 0x2000_0000;
    public const uint IdMask = 0x1FFF_FFFF;
}

/// <summary>canfd_frame.flags bits.</summary>
public static class ZcanCanFdFlags
{
    public const byte Brs = 0x01;
    public const byte Esi = 0x02;
}

/// <summary>ZCAN_Transmit_Data.transmit_type values.</summary>
public static class ZcanTransmitType
{
    public const uint Normal = 0;
    public const uint Once = 1;
}

/// <summary>CAN classic frame (16 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZcanCanFrame
{
    /// <summary>Bits 0-28 id, bit 29 error, bit 30 RTR, bit 31 extended.</summary>
    public uint CanId;
    public byte CanDlc;
    public byte Pad;
    public byte Res0;
    public byte Res1;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public byte[] Data;

    public static ZcanCanFrame Create() => new() { Data = new byte[8] };
}

/// <summary>CAN FD frame (72 bytes). <see cref="Len"/> is the byte count, not a DLC code.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZcanCanFdFrame
{
    /// <summary>Bits 0-28 id, bit 29 error, bit 30 RTR, bit 31 extended.</summary>
    public uint CanId;
    public byte Len;
    /// <summary>Bit 0 BRS, bit 1 ESI.</summary>
    public byte Flags;
    public byte Res0;
    public byte Res1;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] Data;

    public static ZcanCanFdFrame Create() => new() { Data = new byte[64] };
}

/// <summary>ZCAN_Receive_Data (24 bytes): classic frame + microsecond timestamp.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZcanReceiveData
{
    public ZcanCanFrame Frame;
    /// <summary>Receive timestamp in microseconds.</summary>
    public ulong Timestamp;

    public static ZcanReceiveData Create() => new() { Frame = ZcanCanFrame.Create() };
}

/// <summary>ZCAN_ReceiveFD_Data (80 bytes): FD frame + microsecond timestamp.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZcanReceiveFdData
{
    public ZcanCanFdFrame Frame;
    /// <summary>Receive timestamp in microseconds.</summary>
    public ulong Timestamp;

    public static ZcanReceiveFdData Create() => new() { Frame = ZcanCanFdFrame.Create() };
}

/// <summary>
/// ZCAN_Transmit_Data (20 bytes on the Windows ABI). Pass a single struct by ref;
/// do not use it as an array element (the Linux header's aligned(8) padding differs).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZcanTransmitData
{
    public ZcanCanFrame Frame;
    public uint TransmitType;
}

/// <summary>
/// ZCAN_TransmitFD_Data (76 bytes on the Windows ABI). Pass a single struct by ref;
/// do not use it as an array element (the Linux header's aligned(8) padding differs).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZcanTransmitFdData
{
    public ZcanCanFdFrame Frame;
    public uint TransmitType;
}

/// <summary>Classic CAN member of ZCAN_CHANNEL_INIT_CONFIG (16 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZcanCanInit
{
    public uint AccCode;
    public uint AccMask;
    public uint Reserved;
    public byte Filter;
    public byte Timing0;
    public byte Timing1;
    public byte Mode;
}

/// <summary>CAN FD member of ZCAN_CHANNEL_INIT_CONFIG (28 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZcanCanFdInit
{
    public uint AccCode;
    public uint AccMask;
    public uint AbitTiming;
    public uint DbitTiming;
    public uint Brp;
    public byte Filter;
    public byte Mode;
    public ushort Pad;
    public uint Reserved;
}

/// <summary>ZCAN_CHANNEL_INIT_CONFIG (32 bytes): can_type tag + union.</summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct ZcanChannelInitConfig
{
    [FieldOffset(0)]
    public uint CanType;

    [FieldOffset(4)]
    public ZcanCanInit Can;

    [FieldOffset(4)]
    public ZcanCanFdInit CanFd;
}

/// <summary>ZCAN_DEVICE_INFO (80 bytes). Call <see cref="Create"/> before passing by ref.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZcanDeviceInfo
{
    public ushort HwVersion;
    public ushort FwVersion;
    public ushort DrVersion;
    public ushort InVersion;
    public ushort IrqNum;
    public byte CanNum;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
    public byte[] SerialNum;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)]
    public byte[] HwType;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public ushort[] Reserved;

    public static ZcanDeviceInfo Create() => new()
    {
        SerialNum = new byte[20],
        HwType = new byte[40],
        Reserved = new ushort[4],
    };
}
