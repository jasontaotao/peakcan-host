using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;

namespace PeakCan.Host.Core.Tests.Xcp.TestKit;

/// <summary>
/// 内存从机（S5-T2/T3 测试基建）：按 T0 从机源码裁决语义模拟——
/// SET_MTA/DOWNLOAD/UPLOAD，MTA 按 nbytes 自增（含 UPLOAD），
/// 直接写内存、无写校验回调；BUSY/写保护/回读篡改/静默可注入。
/// 寻址窗口 = 物理地址低 16 位（64KB 模拟内存）。
/// </summary>
public sealed class MemorySlaveTransport : IXcpTransport
{
    public event Action<CanFrame>? FrameReceived;
    public long FramesDropped => 0;
    public byte[] Memory { get; } = new byte[0x10000];
    public int BusyDownloadsRemaining { get; set; }
    public bool CorruptReadBack { get; set; }
    public bool Silent { get; set; }
    public bool WriteProtectedOnce { get; set; }
    public int DownloadCount { get; private set; }

    private uint _mta;

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        var d = frame.Data.Span;
        byte[] response;
        switch (d[0])
        {
            case 0xF6: // SET_MTA: [F6,00,00,addrExt,addr LE]
                _mta = BitConverter.ToUInt32(d.Slice(4, 4)) & 0xFFFF;
                response = [0xFF];
                break;
            case 0xF0: // DOWNLOAD: [F0,00,00,n,data...,pad]
            {
                var n = d[3];
                if (WriteProtectedOnce)
                {
                    WriteProtectedOnce = false;
                    response = [0xFE, 0x23]; // ERR_WRITE_PROTECTED
                }
                else if (BusyDownloadsRemaining > 0)
                {
                    BusyDownloadsRemaining--;
                    response = [0xFE, 0x10]; // ERR_CMD_BUSY
                }
                else
                {
                    d.Slice(4, n).ToArray().CopyTo(Memory.AsSpan((int)_mta));
                    _mta += n; // T0 裁决：从机 MTA 按 nbytes 自增
                    DownloadCount++;
                    response = [0xFF];
                }
                break;
            }
            case 0xF5: // UPLOAD: [F5,00,00,n,...]
            {
                var n = d[3];
                var payload = Memory.AsSpan((int)_mta, n).ToArray();
                if (CorruptReadBack)
                    payload[0] ^= 0xFF;
                var resp = new byte[1 + n];
                resp[0] = 0xFF;
                payload.CopyTo(resp, 1);
                response = resp;
                _mta += n; // XCP UPLOAD 同样 MTA 自增（分块续读）
                break;
            }
            default:
                response = [0xFE, 0x20]; // ERR_CMD_UNKNOWN
                break;
        }

        if (!Silent)
            FrameReceived?.Invoke(new CanFrame(
                new CanId(0x18FFF666, FrameFormat.Extended), response, FrameFlags.None, ChannelId.None, default));
        return ValueTask.FromResult(Result<Unit>.Ok(default));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
