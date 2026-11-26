using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// Raw zlgcan.dll P/Invoke declarations.
/// <para>
/// The ZCAN API is handle-based: <c>ZCAN_OpenDevice</c> returns a
/// <c>DEVICE_HANDLE</c> (0 = failure), <c>ZCAN_InitCAN</c> returns a
/// <c>CHANNEL_HANDLE</c> (0 = failure), and every per-channel call takes that
/// channel handle. All functions use <c>__stdcall</c> (FUNC_CALL in zlgcan.h).
/// </para>
/// <para>Test fakes should use <see cref="IZlgApi"/>; this class is the real binding.</para>
/// </summary>
internal static class ZlgNative
{
    private const string Dll = "zlgcan.dll";
    private const CallingConvention Std = CallingConvention.StdCall;

    [DllImport(Dll, CallingConvention = Std)]
    internal static extern IntPtr ZCAN_OpenDevice(uint deviceType, uint deviceIndex, uint reserved);

    [DllImport(Dll, CallingConvention = Std)]
    internal static extern uint ZCAN_CloseDevice(IntPtr deviceHandle);

    [DllImport(Dll, CallingConvention = Std)]
    internal static extern uint ZCAN_GetDeviceInf(IntPtr deviceHandle, ref ZcanDeviceInfo info);

    [DllImport(Dll, CallingConvention = Std)]
    internal static extern IntPtr ZCAN_InitCAN(
        IntPtr deviceHandle, uint canIndex, ref ZcanChannelInitConfig config);

    [DllImport(Dll, CallingConvention = Std)]
    internal static extern uint ZCAN_StartCAN(IntPtr channelHandle);

    [DllImport(Dll, CallingConvention = Std)]
    internal static extern uint ZCAN_ResetCAN(IntPtr channelHandle);

    [DllImport(Dll, CallingConvention = Std)]
    internal static extern uint ZCAN_ClearBuffer(IntPtr channelHandle);

    [DllImport(Dll, CallingConvention = Std)]
    internal static extern uint ZCAN_GetReceiveNum(IntPtr channelHandle, byte type);

    [DllImport(Dll, CallingConvention = Std)]
    internal static extern uint ZCAN_Receive(
        IntPtr channelHandle, [In, Out] ZcanReceiveData[] messages, uint length, int waitTime);

    [DllImport(Dll, CallingConvention = Std)]
    internal static extern uint ZCAN_ReceiveFD(
        IntPtr channelHandle, [In, Out] ZcanReceiveFdData[] messages, uint length, int waitTime);

    [DllImport(Dll, CallingConvention = Std)]
    internal static extern uint ZCAN_Transmit(
        IntPtr channelHandle, ref ZcanTransmitData message, uint length);

    [DllImport(Dll, CallingConvention = Std)]
    internal static extern uint ZCAN_TransmitFD(
        IntPtr channelHandle, ref ZcanTransmitFdData message, uint length);

    // CA2101: path/value 都是 ASCII 常量（"0/canfd_abit_baud_rate" 等），
    // 不存在宽字符到 ANSI 的 best-fit 映射风险；LPStr 与厂商 API 的 const char* 对齐。
    [SuppressMessage(
        "Interoperability",
        "CA2101:Specify marshaling for P/Invoke string arguments",
        Justification = "ZLG SetValue paths/values are ASCII constants.")]
    [DllImport(Dll, CallingConvention = Std, CharSet = CharSet.Ansi)]
    internal static extern uint ZCAN_SetValue(
        IntPtr deviceHandle,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        [MarshalAs(UnmanagedType.LPStr)] string value);
}
