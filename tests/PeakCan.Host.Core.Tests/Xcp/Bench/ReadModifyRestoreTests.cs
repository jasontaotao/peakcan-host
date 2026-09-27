using A2lEditor.Core;
using PeakCan.HIL.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using Microsoft.Extensions.Time.Testing;
using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Bench;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Protocol;
using Xunit;

namespace PeakCan.Host.Core.Tests.Xcp.Bench;

/// <summary>
/// S7-T3：读-改-还原编排骨架（spec D3 安全口径）——保存原值 → 写测试值 → 回读校验 →
/// 还原原值 → 确认还原；写路径唯一入口 = XcpCalibrationWriter（DOWNLOAD 红线）。
/// </summary>
public sealed class ReadModifyRestoreTests
{
    private const uint Logical = 0x1500;

    private static (A2lDocument Doc, ContractSet Contracts) MakeScalarDoc()
    {
        var rl = new A2lRecordLayout("RL_F32",
            new[] { new RecordLayoutEntry("FNC_VALUES", 0, "FLOAT32_IEEE", "COLUMN_SCAL", "DIRECT", null, null) },
            new LineRange(1, 1));
        var ch = new A2lCharacteristic("KmScalar", "d", "VALUE", "RL_F32", Logical,
            "0", "100", null, "CM_ID", new LineRange(1, 1));
        var module = new A2lModule("M", "m",
            Array.Empty<A2lMeasurement>(), new[] { ch }, Array.Empty<A2lAxisPts>(),
            new[] { new A2lCompuMethod("CM_ID", "id", "IDENTICAL", "%.2f", "unit",
                new IdenticalConversion(), new LineRange(10, 10)) },
            new[] { rl }, Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1),
            MemorySegments: new[] { MakeSegment("SEG_A", 0x1500, 0x1500, 256) });
        var doc = new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
        return (doc, new ContractSet(doc));
    }

    private static A2lMemorySegment MakeSegment(
        string name, uint logical, ulong physical, uint length)
    {
        var mappings = new[] { new A2lEditor.Core.IfData.XcpAddressMapping(
            logical, physical, length,
            Array.Empty<A2lEditor.Core.IfData.XcpMissingField>(), string.Empty) };
        var segment = new A2lEditor.Core.IfData.XcpSegment(0, 2, 0, null, null, mappings, 1,
            Array.Empty<A2lEditor.Core.IfData.XcpMissingField>(), string.Empty);
        var ifData = new A2lEditor.Core.IfData.XcpIfData(
            A2lEditor.Core.IfData.XcpIfDataScope.MemorySegmentLevel, null, null, null, null,
            Array.Empty<A2lEditor.Core.IfData.XcpOnCan>(), new[] { segment },
            Array.Empty<A2lUnknownBlock>(), Array.Empty<A2lEditor.Core.IfData.XcpMissingField>(),
            string.Empty);
        return new A2lMemorySegment(name, name, "DATA", "FLASH", "INTERN",
            logical, length, TailFlags, new LineRange(1, 1), ifData);
    }

    private static readonly string[] TailFlags = ["-1", "-1", "-1", "-1", "-1"];

    private static (XcpCalibrationWriter Writer, MemorySlaveTransport Slave, XcpTransportSpy Spy, XcpMaster Master) MakeWriter()
    {
        var slave = new MemorySlaveTransport();
        var spy = new XcpTransportSpy(slave);
        var master = new XcpMaster(spy, new XcpMasterOptions(
            new CanId(0x18FFF667, FrameFormat.Extended), TimeSpan.FromMilliseconds(100), 0));
        var writer = new XcpCalibrationWriter(master, new XcpCalibrationWriterOptions
        {
            BusyRetryCount = 1,
            BusyRetryDelay = TimeSpan.Zero,
            TimeProvider = new FakeTimeProvider(),
        });
        return (writer, slave, spy, master);
    }

    private static void SeedF32(MemorySlaveTransport slave, uint addr, float value)
        => BitConverter.GetBytes(value).CopyTo(slave.Memory, (int)(addr & 0xFFFF));

    private static float ReadF32(MemorySlaveTransport slave, uint addr)
        => BitConverter.ToSingle(slave.Memory, (int)(addr & 0xFFFF));

    [Fact]
    public async Task Happy_path_writes_test_value_then_restores_original()
    {
        var (doc, contracts) = MakeScalarDoc();
        var (writer, slave, spy, master) = MakeWriter();
        SeedF32(slave, Logical, 1.5f);

        var result = await ReadModifyRestore.RunAsync(
            writer, master, contracts.All[0], doc, 9.0f, "demo_scalar_write");

        Assert.True(result.WriteVerified);
        Assert.True(result.Restored);
        Assert.Equal(1.5f, (float)result.OriginalPhysical, 3);

        // 还原后从机内存回到原值；写流量 = 测试写 + 还原写，恰 2 次 DOWNLOAD。
        Assert.Equal(1.5f, ReadF32(slave, Logical), 3);
        var downloads = spy.Sent.Count(f => f.Data.Span[0] == PeakCan.Host.Core.Xcp.Protocol.XcpPid.Download);
        Assert.Equal(2, downloads);
    }

    [Fact]
    public async Task Zero_traffic_rejection_reports_no_restore_needed()
    {
        var (doc, contracts) = MakeScalarDoc();
        var (writer, slave, spy, master) = MakeWriter();
        SeedF32(slave, Logical, 1.5f);

        // 段映射规划失败（addrExt 非 0 不存在映射）→ 零流量拒绝：无变更、无需还原。
        // 用映射外对象地址构造规划失败：改用未覆盖地址的对象。
        var ch = new A2lCharacteristic("KmOut", "d", "VALUE", "RL_F32", 0x9000,
            "0", "100", null, "CM_ID", new LineRange(1, 1));
        var module2 = new A2lModule("M", "m",
            Array.Empty<A2lMeasurement>(), new[] { ch }, Array.Empty<A2lAxisPts>(),
            new[] { new A2lCompuMethod("CM_ID", "id", "IDENTICAL", "%.2f", "unit",
                new IdenticalConversion(), new LineRange(10, 10)) },
            new[] { new A2lRecordLayout("RL_F32",
                new[] { new RecordLayoutEntry("FNC_VALUES", 0, "FLOAT32_IEEE", "COLUMN_SCAL", "DIRECT", null, null) },
                new LineRange(1, 1)) },
            Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1),
            MemorySegments: new[] { MakeSegment("SEG_A", 0x1500, 0x1500, 256) });
        var doc2 = new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module2 }, "", 1);
        var contracts2 = new ContractSet(doc2);
        SeedF32(slave, 0x9000, 2.5f);

        var result = await ReadModifyRestore.RunAsync(
            writer, master, contracts2.All[0], doc2, 9.0f, "uncovered_object");

        Assert.False(result.WriteVerified);
        Assert.True(result.Restored); // 零流量 = 无变更 = 视为已还原
        Assert.Equal(2.5f, ReadF32(slave, 0x9000), 3);
        Assert.Equal(0, spy.WriteCount);
    }

    [Fact]
    public async Task Save_failure_means_zero_write_and_trivial_restore()
    {
        // P1-4 钉 1：静默从机 → 保存原值读超时 → 零写入（DOWNLOAD 0 次），从机未变更。
        var (doc, contracts) = MakeScalarDoc();
        var (writer, slave, spy, master) = MakeWriter();
        SeedF32(slave, Logical, 1.5f);
        slave.Silent = true;

        var result = await ReadModifyRestore.RunAsync(
            writer, master, contracts.All[0], doc, 9.0f, "save_fail");

        Assert.False(result.WriteVerified);
        Assert.True(result.Restored);
        Assert.Contains("零写入", result.Detail);
        Assert.Equal(1.5f, ReadF32(slave, Logical), 3);
        Assert.Equal(0, slave.DownloadCount);
    }

    [Fact]
    public async Task Write_exception_still_restores_original()
    {
        // P1-4 钉 2：写步失败（WRITE_PROTECTED）→ 强制还原路径仍执行。
        var (doc, contracts) = MakeScalarDoc();
        var (writer, slave, spy, master) = MakeWriter();
        SeedF32(slave, Logical, 1.5f);
        slave.WriteProtectedOnce = true; // 消耗在测试值写上

        var result = await ReadModifyRestore.RunAsync(
            writer, master, contracts.All[0], doc, 9.0f, "write_fail_restore");

        Assert.False(result.WriteVerified);
        Assert.True(result.Restored); // 还原写未被保护，成功回到原值
        Assert.Equal(1.5f, ReadF32(slave, Logical), 3);
    }

    [Fact]
    public async Task Restore_failure_is_recorded_fail_loud()
    {
        // P1-4 钉 3：还原也失败 → Restored=false + "需人工检查 ECU"（fail-loud 记报告）。
        // BUSY 注入 4 次：测试值写 2 次（含 1 重试）+ 还原写 2 次（含 1 重试）全 BUSY。
        var (doc, contracts) = MakeScalarDoc();
        var (writer, slave, spy, master) = MakeWriter();
        SeedF32(slave, Logical, 1.5f);
        slave.BusyDownloadsRemaining = 4;

        var result = await ReadModifyRestore.RunAsync(
            writer, master, contracts.All[0], doc, 9.0f, "restore_fail");

        Assert.False(result.WriteVerified);
        Assert.False(result.Restored);
        Assert.Contains("需人工检查 ECU", result.Detail);
        // 从机内容未变（写从未成功）——但结论口径仍是 Restored=false（不确定即按失败处理）。
    }

    [Fact]
    public async Task Undecodable_original_yields_nan_fact_and_report_still_fresh()
    {
        // R2 钉（P1-3）：TAB_VERB 换算对任意 raw 不可解 → 原值 Decode 异常不外溢，
        // OriginalPhysical=NaN + Detail 归因，报告照常产出（还原走镜像字节）。
        var (doc, contracts) = MakeTabVerbDoc();
        var (writer, slave, spy, master) = MakeWriter();

        var result = await ReadModifyRestore.RunAsync(
            writer, master, contracts.All[0], doc, 1.0f, "tab_verb_undecodable");

        Assert.True(double.IsNaN(result.OriginalPhysical));
        Assert.Contains("换算不可解", result.Detail);
        Assert.True(result.Restored);
    }

    /// <summary>TAB_VERB 标量夹具——Decode 对任意 raw 都抛 DecodeException（ConversionUnsupported）。</summary>
    private static (A2lEditor.Core.Model.A2lDocument Doc, A2lEditor.Core.Layout.ContractSet Contracts) MakeTabVerbDoc()
    {
        var rl = new A2lRecordLayout("RL_F32",
            new[] { new RecordLayoutEntry("FNC_VALUES", 0, "FLOAT32_IEEE", "COLUMN_SCAL", "DIRECT", null, null) },
            new LineRange(1, 1));
        var ch = new A2lCharacteristic("KmText", "d", "VALUE", "RL_F32", 0x1500,
            "0", "100", null, "CM_TEXT", new LineRange(1, 1));
        var module = new A2lModule("M", "m",
            Array.Empty<A2lMeasurement>(), new[] { ch }, Array.Empty<A2lAxisPts>(),
            new[]
            {
                new A2lCompuMethod("CM_TEXT", "tab", "TAB_VERB", "%.0f", "state",
                    new TabVerbConversion(true,
                        new[] { new CompuAxisPoint(0, "Off"), new CompuAxisPoint(1, "On") },
                        false),
                    new LineRange(10, 10)),
            },
            new[] { rl }, Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1),
            MemorySegments: new[] { BenchDocs.MakeSegment("SEG_A", 0x1500, 0x1500, 4) });
        var doc = new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
        return (doc, new A2lEditor.Core.Layout.ContractSet(doc));
    }
}
