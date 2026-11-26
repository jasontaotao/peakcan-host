using System.Runtime.InteropServices;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// Injectable seam over the zlgcan.dll ZCAN API. Production uses
/// <see cref="ZlgNativeApi"/>; tests supply a fake to assert the exact call
/// order and handle semantics without hardware.
/// </summary>
public interface IZlgApi
{
    /// <summary>Returns the device handle, or <see cref="IntPtr.Zero"/> on failure.</summary>
    IntPtr OpenDevice(uint deviceType, uint deviceIndex, uint reserved);

    uint CloseDevice(IntPtr deviceHandle);

    uint GetDeviceInf(IntPtr deviceHandle, ref ZcanDeviceInfo info);

    /// <summary>Returns the channel handle, or <see cref="IntPtr.Zero"/> on failure.</summary>
    IntPtr InitCan(IntPtr deviceHandle, uint canIndex, ref ZcanChannelInitConfig config);

    uint StartCan(IntPtr channelHandle);

    uint ResetCan(IntPtr channelHandle);

    uint ClearBuffer(IntPtr channelHandle);

    uint GetReceiveNum(IntPtr channelHandle, byte type);

    uint Receive(
        IntPtr channelHandle, [In, Out] ZcanReceiveData[] messages, uint length, int waitTime);

    uint ReceiveFd(
        IntPtr channelHandle, [In, Out] ZcanReceiveFdData[] messages, uint length, int waitTime);

    uint Transmit(IntPtr channelHandle, ref ZcanTransmitData message, uint length);

    uint TransmitFd(IntPtr channelHandle, ref ZcanTransmitFdData message, uint length);

    uint SetValue(IntPtr deviceHandle, string path, string value);
}
