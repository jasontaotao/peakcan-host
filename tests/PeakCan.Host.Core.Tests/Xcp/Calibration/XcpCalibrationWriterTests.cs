using A2lEditor.Core;
using A2lEditor.Core.Layout;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Protocol;
using Xunit;

namespace PeakCan.Host.Core.Tests.Xcp.Calibration;

/// <summary>
/// S5-T2：XcpCalibrationWriter 写回内核（spec D1/D4 + T0 裁决）——
/// SET_MTA + DOWNLOAD×⌈n/4⌉（MTA 自增，从机源码证实）+ 重臂 SET_MTA + UPLOAD 回读逐字节比对；
/// BUSY 重试（D1）；非 BUSY 负响应不重试；拒绝面零线上流量（流量审计）。
/// 契约取真机 fixture（tests TestData App_merge_INCA.a2l）。
/// </summary>
public sealed class XcpCalibrationWriterTests
{
    private static readonly CanId SlaveId = new(0x18FFF666, FrameFormat.Extended);
    private const uint Addr = 0x2000_1500;

    private static readonly Lazy<ContractSet> Fixture = new(() =>
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l");
        var parsed = Asap2PackageApi.ParseFile(path);
        Assert.NotNull(parsed.Value);
        return Asap2PackageApi.Contracts(parsed.Value!);
    });

    /// <summary>按字节长度选一个标定契约（元素 DataType 非空）。</summary>
    private static ValueContract CalContract(int byteLength)
    {
        var contract = Fixture.Value.All.FirstOrDefault(c =>
            c.Category == A2lObjectCategory.Characteristic
            && c.DataType is not null
            && c.TotalByteLength == byteLength);
        Assert.NotNull(contract);
        return contract!;
    }

    // ============ 内存从机（T0 裁决语义：MTA 自增、直接写内存、无写校验回调） ============
    private sealed class MemorySlaveTransport : IXcpTransport
    {
        public event Action<CanFrame>? FrameReceived;
        public long FramesDropped => 0;
        public readonly byte[] Memory = new byte[0x10000];
        public int BusyDownloadsRemaining;
        public bool CorruptReadBack;
        public bool Silent;
        public bool WriteProtectedOnce;
        public int DownloadCount;

        private uint _mta;

        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            var d = frame.Data.Span;
            byte[] response;
            switch (d[0])
            {
                case 0xF6: // SET_MTA: [F6,00,00,addrExt,addr LE]
                    _mta = BitConverter.ToUInt32(d.Slice(4, 4)) & 0xFFFF; // 模拟内存 64KB 窗口（地址低 16 位）
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
                FrameReceived?.Invoke(new CanFrame(SlaveId, response, FrameFlags.None, ChannelId.None, default));
            return ValueTask.FromResult(Result<Unit>.Ok(default));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (XcpCalibrationWriter Writer, MemorySlaveTransport Slave, XcpTransportSpy Spy) MakeWriter(
        int busyRetries = 1, TimeSpan? timeout = null)
    {
        var slave = new MemorySlaveTransport();
        var spy = new XcpTransportSpy(slave);
        var master = new XcpMaster(spy, new XcpMasterOptions(
            new CanId(0x18FFF667, FrameFormat.Extended), timeout ?? TimeSpan.FromMilliseconds(100), 0));
        var writer = new XcpCalibrationWriter(master, new XcpCalibrationWriterOptions
        {
            BusyRetryCount = busyRetries,
            BusyRetryDelay = TimeSpan.Zero, // FakeTimeProvider 不自动推时钟，零退避避免测试挂死
            TimeProvider = new FakeTimeProvider(),
        });
        return (writer, slave, spy);
    }

    [Fact]
    public async Task Write_1byte_value_sets_mta_downloads_and_readback_matches()
    {
        var (writer, slave, spy) = MakeWriter();
        var contract = CalContract(1);

        var outcome = await writer.WriteAsync(contract, Addr, 200);

        Assert.Equal(CalibrationWriteStatus.Written, outcome.Status);
        Assert.Equal(1, slave.DownloadCount);
        Assert.Equal((byte)200, slave.Memory[Addr & 0xFFFF]);
        // 流量审计：SET_MTA + DOWNLOAD + SET_MTA(重臂) + UPLOAD = 4 帧
        Assert.Equal(4, spy.WriteCount);
    }

    [Fact]
    public async Task Write_8byte_value_slices_into_two_downloads_with_mta_autoincrement()
    {
        var (writer, slave, _) = MakeWriter();
        var contract = CalContract(8);
        Assert.Equal(8, contract.TotalByteLength);

        var outcome = await writer.WriteAsync(contract, Addr, 3.14);

        Assert.Equal(CalibrationWriteStatus.Written, outcome.Status);
        Assert.Equal(2, slave.DownloadCount); // 8B = 4B + 4B（MTA 自增续写）
        var expected = new byte[8];
        contract.Encode(3.14, expected);
        Assert.Equal(expected, slave.Memory.AsSpan((int)(Addr & 0xFFFF), 8).ToArray());
    }

    [Fact]
    public async Task Busy_negative_response_is_retried_then_written()
    {
        var (writer, slave, _) = MakeWriter(busyRetries: 1);
        var contract = CalContract(1);
        slave.BusyDownloadsRemaining = 1;

        var outcome = await writer.WriteAsync(contract, Addr, 100);

        Assert.Equal(CalibrationWriteStatus.Written, outcome.Status);
        Assert.Equal(1, slave.DownloadCount); // BUSY 那次不计成功写
        Assert.Equal(0x64, slave.Memory[Addr & 0xFFFF]);
    }

    [Fact]
    public async Task Write_protected_fails_without_retry()
    {
        var (writer, slave, spy) = MakeWriter();
        var contract = CalContract(1);
        slave.WriteProtectedOnce = true;

        var outcome = await writer.WriteAsync(contract, Addr, 1);

        Assert.Equal(CalibrationWriteStatus.WriteFailed, outcome.Status);
        Assert.Equal(0, slave.DownloadCount);
        Assert.Equal(2, spy.WriteCount); // SET_MTA + DOWNLOAD 负响应（无重试）
    }

    [Fact]
    public async Task Readback_mismatch_is_reported()
    {
        var (writer, slave, _) = MakeWriter();
        var contract = CalContract(1);
        slave.CorruptReadBack = true;

        var outcome = await writer.WriteAsync(contract, Addr, 50);

        Assert.Equal(CalibrationWriteStatus.ReadBackMismatch, outcome.Status);
    }

    [Fact]
    public async Task Measurement_contract_is_rejected_with_zero_traffic()
    {
        // 宁可不写不错写：非标定对象（MEASUREMENT）拒绝且零线上流量。
        var (writer, _, spy) = MakeWriter();
        var measurement = Fixture.Value.All.First(c => c.Category == A2lObjectCategory.Measurement);

        var outcome = await writer.WriteAsync(measurement, Addr, 1);

        Assert.Equal(CalibrationWriteStatus.Rejected, outcome.Status);
        Assert.Equal(0, spy.WriteCount);
    }

    [Fact]
    public async Task Silent_slave_times_out_and_fails()
    {
        var (writer, slave, _) = MakeWriter(timeout: TimeSpan.FromMilliseconds(20));
        var contract = CalContract(1);
        slave.Silent = true;

        var outcome = await writer.WriteAsync(contract, Addr, 7);

        Assert.Equal(CalibrationWriteStatus.WriteFailed, outcome.Status);
    }
}
