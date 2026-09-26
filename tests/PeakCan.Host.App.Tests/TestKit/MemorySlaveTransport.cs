using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;

namespace PeakCan.Host.App.Tests.TestKit;

/// <summary>
/// 内存从机（S5-T4 App 测试基建；Core.Tests 同款副本）：T0 从机源码裁决语义——
/// MTA 按 nbytes 自增（含 UPLOAD）、直接写内存、无写校验回调。
/// 寻址窗口 = 物理地址低 16 位。
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
            case 0xF6:
                _mta = BitConverter.ToUInt32(d.Slice(4, 4)) & 0xFFFF;
                response = [0xFF];
                break;
            case 0xF0:
            {
                var n = d[3];
                if (WriteProtectedOnce)
                {
                    WriteProtectedOnce = false;
                    response = [0xFE, 0x23];
                }
                else if (BusyDownloadsRemaining > 0)
                {
                    BusyDownloadsRemaining--;
                    response = [0xFE, 0x10];
                }
                else
                {
                    d.Slice(4, n).ToArray().CopyTo(Memory.AsSpan((int)_mta));
                    _mta += n;
                    DownloadCount++;
                    response = [0xFF];
                }
                break;
            }
            case 0xF5:
            {
                var n = d[3];
                var payload = Memory.AsSpan((int)_mta, n).ToArray();
                if (CorruptReadBack)
                    payload[0] ^= 0xFF;
                var resp = new byte[1 + n];
                resp[0] = 0xFF;
                payload.CopyTo(resp, 1);
                response = resp;
                _mta += n;
                break;
            }
            default:
                response = [0xFE, 0x20];
                break;
        }

        if (!Silent)
            FrameReceived?.Invoke(new CanFrame(
                new CanId(0x18FFF666, FrameFormat.Extended), response, FrameFlags.None, ChannelId.None, default));
        return ValueTask.FromResult(Result<Unit>.Ok(default));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
