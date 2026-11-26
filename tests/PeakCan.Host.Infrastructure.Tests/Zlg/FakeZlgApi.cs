using System.Text;
using PeakCan.Host.Infrastructure.Zlg;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// 手写 fake，记录每个 ZCAN 调用的顺序与参数。测试用它验证句柄语义，
/// 不加载真实 zlgcan.dll。
/// </summary>
internal sealed class FakeZlgApi : IZlgApi
{
    public Dictionary<(uint DevType, uint DevIdx), IntPtr> Devices { get; } = new();

    public IntPtr DefaultDeviceHandle { get; set; } = (IntPtr)0x1000;
    public IntPtr InitCanHandle { get; set; } = (IntPtr)0x2000;
    public uint GetDeviceInfResult { get; set; } = ZlgError.Success;
    public byte CanNum { get; set; } = 2;
    public string HwType { get; set; } = "USBCANFD-200U";
    public uint StartCanResult { get; set; } = ZlgError.Success;
    public uint SetValueResult { get; set; } = ZlgError.Success;
    public uint TransmitResult { get; set; } = ZlgError.Success;
    public uint TransmitFdResult { get; set; } = ZlgError.Success;
    public uint GetReceiveNumCanResult { get; set; }
    public uint GetReceiveNumFdResult { get; set; }
    public uint ReceiveResult { get; set; }
    public uint ReceiveFdResult { get; set; }

    public Exception? OpenDeviceException { get; set; }
    public Exception? GetDeviceInfException { get; set; }
    public Exception? ReceiveException { get; set; }
    public Exception? ReceiveFdException { get; set; }

    /// <summary>按调用顺序记录 open/close/init/start/reset/setvalue/transmit。</summary>
    public List<string> Calls { get; } = new();
    public Func<ZcanReceiveData>? ClassicFrameFactory { get; set; }
    public Func<ZcanReceiveFdData>? FdFrameFactory { get; set; }

    /// <summary>InitCan 内同步调用的钩子；测试用它把 Connect 卡在临界区外。</summary>
    public Action? BeforeInitCan { get; set; }

    public List<(uint DevType, uint DevIdx)> OpenCalls { get; } = new();
    public List<IntPtr> CloseCalls { get; } = new();
    public List<(IntPtr DeviceHandle, string Path, string Value)> SetValueCalls { get; } = new();
    public List<(IntPtr DeviceHandle, uint CanIndex, ZcanChannelInitConfig Config)> InitCanCalls { get; } = new();
    public List<IntPtr> StartCanCalls { get; } = new();
    public List<IntPtr> ResetCanCalls { get; } = new();
    public List<(IntPtr ChannelHandle, uint Length)> TransmitCalls { get; } = new();
    public List<(IntPtr ChannelHandle, uint Length)> TransmitFdCalls { get; } = new();
    public List<(IntPtr ChannelHandle, byte Type)> GetReceiveNumCalls { get; } = new();
    public List<(IntPtr ChannelHandle, uint Length, int WaitTime)> ReceiveCalls { get; } = new();
    public List<(IntPtr ChannelHandle, uint Length, int WaitTime)> ReceiveFdCalls { get; } = new();

    public IntPtr OpenDevice(uint deviceType, uint deviceIndex, uint reserved)
    {
        Calls.Add("open");
        OpenCalls.Add((deviceType, deviceIndex));
        if (OpenDeviceException is not null) throw OpenDeviceException;
        if (Devices.TryGetValue((deviceType, deviceIndex), out var handle))
            return handle;
        return IntPtr.Zero;
    }

    public uint CloseDevice(IntPtr deviceHandle)
    {
        Calls.Add("close");
        CloseCalls.Add(deviceHandle);
        return ZlgError.Success;
    }

    public uint GetDeviceInf(IntPtr deviceHandle, ref ZcanDeviceInfo info)
    {
        if (GetDeviceInfException is not null) throw GetDeviceInfException;
        if (GetDeviceInfResult != ZlgError.Success) return GetDeviceInfResult;

        info.SerialNum ??= new byte[20];
        info.HwType ??= new byte[40];
        info.Reserved ??= new ushort[4];
        info.CanNum = CanNum;
        var bytes = Encoding.ASCII.GetBytes(HwType);
        Array.Copy(bytes, info.HwType, Math.Min(bytes.Length, info.HwType.Length));
        return ZlgError.Success;
    }

    public IntPtr InitCan(IntPtr deviceHandle, uint canIndex, ref ZcanChannelInitConfig config)
    {
        BeforeInitCan?.Invoke();
        Calls.Add("init");
        InitCanCalls.Add((deviceHandle, canIndex, config));
        return InitCanHandle;
    }

    public uint StartCan(IntPtr channelHandle)
    {
        Calls.Add("start");
        StartCanCalls.Add(channelHandle);
        return StartCanResult;
    }

    public uint ResetCan(IntPtr channelHandle)
    {
        Calls.Add("reset");
        ResetCanCalls.Add(channelHandle);
        return ZlgError.Success;
    }

    public uint ClearBuffer(IntPtr channelHandle) => ZlgError.Success;

    public uint GetReceiveNum(IntPtr channelHandle, byte type)
    {
        GetReceiveNumCalls.Add((channelHandle, type));
        return type == ZcanReceiveType.Can ? GetReceiveNumCanResult : GetReceiveNumFdResult;
    }

    public uint Receive(
        IntPtr channelHandle, ZcanReceiveData[] messages, uint length, int waitTime)
    {
        ReceiveCalls.Add((channelHandle, length, waitTime));
        if (ReceiveException is not null) throw ReceiveException;
        var read = Math.Min(ReceiveResult, length);
        if (read > 0 && messages.Length > 0 && ClassicFrameFactory is not null)
            messages[0] = ClassicFrameFactory();
        return read;
    }

    public uint ReceiveFd(
        IntPtr channelHandle, ZcanReceiveFdData[] messages, uint length, int waitTime)
    {
        ReceiveFdCalls.Add((channelHandle, length, waitTime));
        if (ReceiveFdException is not null) throw ReceiveFdException;
        var read = Math.Min(ReceiveFdResult, length);
        if (read > 0 && messages.Length > 0 && FdFrameFactory is not null)
            messages[0] = FdFrameFactory();
        return read;
    }

    public uint Transmit(IntPtr channelHandle, ref ZcanTransmitData message, uint length)
    {
        Calls.Add("transmit");
        TransmitCalls.Add((channelHandle, length));
        return TransmitResult;
    }

    public uint TransmitFd(IntPtr channelHandle, ref ZcanTransmitFdData message, uint length)
    {
        Calls.Add("transmitfd");
        TransmitFdCalls.Add((channelHandle, length));
        return TransmitFdResult;
    }

    public uint SetValue(IntPtr deviceHandle, string path, string value)
    {
        Calls.Add("setvalue");
        SetValueCalls.Add((deviceHandle, path, value));
        return SetValueResult;
    }
}
