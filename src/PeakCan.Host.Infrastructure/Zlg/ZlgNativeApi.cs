namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>Production <see cref="IZlgApi"/> that forwards to the raw P/Invoke bindings.</summary>
public sealed class ZlgNativeApi : IZlgApi
{
    public IntPtr OpenDevice(uint deviceType, uint deviceIndex, uint reserved)
        => ZlgNative.ZCAN_OpenDevice(deviceType, deviceIndex, reserved);

    public uint CloseDevice(IntPtr deviceHandle)
        => ZlgNative.ZCAN_CloseDevice(deviceHandle);

    public uint GetDeviceInf(IntPtr deviceHandle, ref ZcanDeviceInfo info)
        => ZlgNative.ZCAN_GetDeviceInf(deviceHandle, ref info);

    public IntPtr InitCan(IntPtr deviceHandle, uint canIndex, ref ZcanChannelInitConfig config)
        => ZlgNative.ZCAN_InitCAN(deviceHandle, canIndex, ref config);

    public uint StartCan(IntPtr channelHandle)
        => ZlgNative.ZCAN_StartCAN(channelHandle);

    public uint ResetCan(IntPtr channelHandle)
        => ZlgNative.ZCAN_ResetCAN(channelHandle);

    public uint ClearBuffer(IntPtr channelHandle)
        => ZlgNative.ZCAN_ClearBuffer(channelHandle);

    public uint GetReceiveNum(IntPtr channelHandle, byte type)
        => ZlgNative.ZCAN_GetReceiveNum(channelHandle, type);

    public uint Receive(
        IntPtr channelHandle, ZcanReceiveData[] messages, uint length, int waitTime)
        => ZlgNative.ZCAN_Receive(channelHandle, messages, length, waitTime);

    public uint ReceiveFd(
        IntPtr channelHandle, ZcanReceiveFdData[] messages, uint length, int waitTime)
        => ZlgNative.ZCAN_ReceiveFD(channelHandle, messages, length, waitTime);

    public uint Transmit(IntPtr channelHandle, ref ZcanTransmitData message, uint length)
        => ZlgNative.ZCAN_Transmit(channelHandle, ref message, length);

    public uint TransmitFd(IntPtr channelHandle, ref ZcanTransmitFdData message, uint length)
        => ZlgNative.ZCAN_TransmitFD(channelHandle, ref message, length);

    public uint SetValue(IntPtr deviceHandle, string path, string value)
        => ZlgNative.ZCAN_SetValue(deviceHandle, path, value);
}
