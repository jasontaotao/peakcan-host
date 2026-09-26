using A2lEditor.Core;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using FluentAssertions;
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

    /// <summary>内存合成 FLOAT64 单元素 VALUE 对象（TotalByteLength 8 = 元素 8B，单元素合法）。</summary>
    private static ValueContract Float64Contract()
    {
        var rl = new A2lRecordLayout("RL_F64",
            new[] { new RecordLayoutEntry("FNC_VALUES", 0, "FLOAT64_IEEE", "COLUMN_SCAL", "DIRECT", null, null) },
            new LineRange(1, 1));
        var ch = new A2lCharacteristic("Ch64", "d", "VALUE", "RL_F64", 0x2000_1500,
            "0", "100", null, "CM_ID", new LineRange(1, 1));
        var module = new A2lModule("M", "m",
            Array.Empty<A2lMeasurement>(), new[] { ch }, Array.Empty<A2lAxisPts>(),
            new[]
            {
                new A2lCompuMethod("CM_ID", "id", "IDENTICAL", "%.2f", "unit",
                    new IdenticalConversion(), new LineRange(10, 10)),
            },
            new[] { rl }, Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1));
        var doc = new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
        var contracts = new ContractSet(doc);
        contracts.TryGet("Ch64", out var contract).Should().BeTrue();
        return contract!;
    }

    [Fact]
    public async Task Write_8byte_value_slices_into_two_downloads_with_mta_autoincrement()
    {
        var (writer, slave, _) = MakeWriter();
        var contract = Float64Contract();
        Assert.Equal(8, contract.TotalByteLength);

        var outcome = await writer.WriteAsync(contract, Addr, 3.14);

        Assert.Equal(CalibrationWriteStatus.Written, outcome.Status);
        Assert.Equal(2, slave.DownloadCount); // 8B = 4B + 4B（MTA 自增续写）
        var expected = new byte[8];
        contract.Encode(3.14, expected);
        Assert.Equal(expected, slave.Memory.AsSpan((int)(Addr & 0xFFFF), 8).ToArray());
    }

    [Fact]
    public async Task Multi_element_object_is_rejected_with_zero_traffic()
    {
        // S5 评审 P1-1 回归钉：多元素对象（VAL_BLK 2×F32）Encode 只写首元素，
        // 其余元素会被静默清零——v0.1 拒绝且零线上流量。
        var (writer, _, spy) = MakeWriter();
        var contract = CalContract(8);
        Assert.True(contract.TotalByteLength > ByteLayout.SizeOf(contract.DataType!.Value));

        var outcome = await writer.WriteAsync(contract, Addr, 3.14);

        Assert.Equal(CalibrationWriteStatus.Rejected, outcome.Status);
        Assert.Equal(0, spy.WriteCount);
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
