namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// 读抽象缝：ZLG 经典 + FD 帧的阻塞读取。
/// 生产环境注入 <see cref="ZlgReader"/>；测试注入 fake。
/// </summary>
public interface IZlgReader
{
    /// <summary>读取一帧经典 CAN 数据。返回非 0 表示成功，0 表示超时/无数据。</summary>
    uint ReadClassic(uint devType, uint devIdx, uint canIdx, out ZlgCanMsg msg);

    /// <summary>读取一帧 CAN FD 数据。返回非 0 表示成功，0 表示超时/无数据。</summary>
    uint ReadFd(uint devType, uint devIdx, uint canIdx, out ZlgCanFdMsg msg);
}

/// <summary>生产环境 ZLG 读操作，委托 zlgcan.dll。</summary>
public sealed class ZlgReader : IZlgReader
{
    private const int WaitTimeMs = 10;

    public uint ReadClassic(uint devType, uint devIdx, uint canIdx, out ZlgCanMsg msg)
    {
        var buf = new ZlgCanMsg[1];
        var ret = ZlgNative.ZCAN_Receive(devType, devIdx, canIdx, buf, 1, WaitTimeMs);
        msg = ret > 0 ? buf[0] : default;
        return ret;
    }

    public uint ReadFd(uint devType, uint devIdx, uint canIdx, out ZlgCanFdMsg msg)
    {
        var buf = new ZlgCanFdMsg[1];
        var ret = ZlgNative.ZCAN_ReceiveFD(devType, devIdx, canIdx, buf, 1, WaitTimeMs);
        msg = ret > 0 ? buf[0] : default;
        return ret;
    }
}