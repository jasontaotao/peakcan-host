using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Protocol;
using Xunit;

namespace PeakCan.Host.Core.Tests.Xcp.Calibration;

/// <summary>
/// S6-T6 多段写扩展（spec D6）：对象逻辑区间按 MEMORY_SEGMENT ADDRESS_MAPPING
/// 切 run 写 + 分段回读比对；多元素广播语义（S5 P1-1 枚举注释预告"S6 扩元素广播"——
/// Encode 只写首元素的问题以"同值广播全元素"解除，不再静默清零）。
/// 单段失败中断后续 run；拒绝面（未覆盖/addrExt≠0/长度非元素整数倍）零线上流量。
/// </summary>
public sealed class CalibrationMultiSegmentWriteTests
{
    private const uint Logical = 0x1500; // 从机按低 16 位寻址

    private static readonly CanId SlaveId = new(0x18FFF666, FrameFormat.Extended);

    /// <summary>VAL_BLK 2×F32 @0x1500：逻辑区间 [0x1500,0x1508) 跨两个映射。</summary>
    private static (A2lDocument Doc, ContractSet Contracts) MakeCrossDoc(
        uint? secondMappingPhysical = 0x3000, uint? secondAddrExt = null, bool gapAfterFirst = false)
    {
        var rl = new A2lRecordLayout("RL_F32",
            new[] { new RecordLayoutEntry("FNC_VALUES", 0, "FLOAT32_IEEE", "COLUMN_SCAL", "DIRECT", null, null) },
            new LineRange(1, 1));
        var ch = new A2lCharacteristic("KmMap", "d", "VAL_BLK", "RL_F32", Logical,
            "0", "100", null, "CM_ID", new LineRange(1, 1), MatrixDim: new uint[] { 2 });
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
            new LineRange(1, 1),
            MemorySegments: new[]
            {
                MakeSegment("SEG_A", 0x1500, 0x1500, 4),
                gapAfterFirst
                    ? MakeSegment("SEG_B", 0x1504, 0x3000, 4, skipMapping: true)
                    : MakeSegment("SEG_B", 0x1504, secondMappingPhysical ?? 0x3000, 4, secondAddrExt),
            });
        var doc = new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
        var contracts = new ContractSet(doc);
        return (doc, contracts);
    }

    private static A2lMemorySegment MakeSegment(
        string name, uint logical, uint physical, uint length,
        uint? addressExtension = null, bool skipMapping = false)
    {
        var mappings = skipMapping
            ? Array.Empty<XcpAddressMapping>()
            : new[] { new XcpAddressMapping(logical, physical, length,
                Array.Empty<XcpMissingField>(), string.Empty) };
        var segment = new XcpSegment(0, 2, addressExtension, null, null, mappings, 1,
            Array.Empty<XcpMissingField>(), string.Empty);
        var ifData = new XcpIfData(XcpIfDataScope.MemorySegmentLevel, null, null, null, null,
            Array.Empty<XcpOnCan>(), new[] { segment }, Array.Empty<A2lUnknownBlock>(),
            Array.Empty<XcpMissingField>(), string.Empty);
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

    // ---------------- PlanWriteRuns ----------------

    [Fact]
    public void Plan_runs_single_mapping_is_one_run()
    {
        var (doc, _) = MakeCrossDoc();
        var runs = CalibrationRunPlanner.PlanWriteRuns(doc, Logical, 4);
        Assert.NotNull(runs);
        var run = Assert.Single(runs!);
        Assert.Equal(0x1500u, run.PhysicalAddress);
        Assert.Equal(4, run.ByteLength);
        Assert.Equal(0, run.SourceOffset);
    }

    [Fact]
    public void Plan_runs_cross_boundary_splits_into_two_runs()
    {
        var (doc, _) = MakeCrossDoc();
        var runs = CalibrationRunPlanner.PlanWriteRuns(doc, Logical, 8);
        Assert.NotNull(runs);
        Assert.Equal(2, runs!.Count);
        Assert.Equal((0x1500u, 4, 0), (runs[0].PhysicalAddress, runs[0].ByteLength, runs[0].SourceOffset));
        Assert.Equal((0x3000u, 4, 4), (runs[1].PhysicalAddress, runs[1].ByteLength, runs[1].SourceOffset));
    }

    [Fact]
    public void Plan_runs_gap_or_addr_ext_is_null()
    {
        var (gapDoc, _) = MakeCrossDoc(gapAfterFirst: true);
        Assert.Null(CalibrationRunPlanner.PlanWriteRuns(gapDoc, Logical, 8));

        var (extDoc, _) = MakeCrossDoc(secondAddrExt: 1);
        Assert.Null(CalibrationRunPlanner.PlanWriteRuns(extDoc, Logical, 8));

        // 部分覆盖（只要 5B，第二 run 只需 1B 仍在映射内）→ 不为 null；但完全越界为 null
        Assert.Null(CalibrationRunPlanner.PlanWriteRuns(MakeCrossDoc().Doc, 0x9000, 8));
    }

    // ---------------- writer：跨段写 + 广播 ----------------

    [Fact]
    public async Task Cross_boundary_object_broadcasts_value_across_both_runs()
    {
        var (doc, contracts) = MakeCrossDoc();
        var (writer, slave, spy, _) = MakeWriter();
        contracts.TryGet("KmMap", out var contract).Should().BeTrue();

        var outcome = await writer.WriteAsync(contract!, doc, 3.5);

        Assert.Equal(CalibrationWriteStatus.Written, outcome.Status);
        var element = new byte[4];
        contract!.Encode(3.5, element);
        Assert.Equal(element, slave.Memory.AsSpan(0x1500, 4).ToArray());
        Assert.Equal(element, slave.Memory.AsSpan(0x3000, 4).ToArray());
        // 流量审计：每 run = SET_MTA + DOWNLOAD + SET_MTA(重臂) + UPLOAD = 4 帧 ×2 = 8
        Assert.Equal(8, spy.WriteCount);
        Assert.Equal(2, slave.DownloadCount);
        Assert.Contains("广播", outcome.Detail);
    }

    [Fact]
    public async Task First_run_failure_aborts_remaining_traffic()
    {
        var (doc, contracts) = MakeCrossDoc();
        var (writer, slave, spy, _) = MakeWriter();
        contracts.TryGet("KmMap", out var contract).Should().BeTrue();
        slave.WriteProtectedOnce = true; // 第一 run 的 DOWNLOAD 即负响应

        var outcome = await writer.WriteAsync(contract!, doc, 3.5);

        Assert.Equal(CalibrationWriteStatus.WriteFailed, outcome.Status);
        // 中断语义：SET_MTA + DOWNLOAD 负响应后不再发后续帧（无重试、无第二 run）
        Assert.Equal(2, spy.WriteCount);
        Assert.Equal(0u, MemoryU32(slave, 0x3000)); // 第二 run 区域未被触碰（保持 0）
    }

    [Fact]
    public async Task Unmapped_range_is_rejected_with_zero_traffic()
    {
        var (gapDoc, contracts) = MakeCrossDoc(gapAfterFirst: true);
        var (writer, _, spy, _) = MakeWriter();
        contracts.TryGet("KmMap", out var contract).Should().BeTrue();

        var outcome = await writer.WriteAsync(contract!, gapDoc, 3.5);

        Assert.Equal(CalibrationWriteStatus.Rejected, outcome.Status);
        Assert.Equal(0, spy.WriteCount);
    }

    private static uint MemoryU32(MemorySlaveTransport slave, uint addr)
        => BitConverter.ToUInt32(slave.Memory, (int)(addr & 0xFFFF));

    // ---------------- reconciler：门禁解除端到端 ----------------

    [Fact]
    public async Task Reconciler_applies_multi_element_cross_boundary_object()
    {
        var (doc, contracts) = MakeCrossDoc();
        var (writer, slave, _, master) = MakeWriter();
        contracts.TryGet("KmMap", out var contract).Should().BeTrue();

        var set = CalibrationParameterSet.Export(
            new[] { new CalibrationEntry("KmMap", 3.5, null, null) }, "AAAA", "test");
        await using var reconciler = new CalibrationReconciler(writer, master);

        var report = await reconciler.ApplyAsync(set, contracts);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CalibrationEntryStatus.Written, entry.Status);
        var element = new byte[4];
        contract!.Encode(3.5, element);
        Assert.Equal(element, slave.Memory.AsSpan(0x1500, 4).ToArray());
        Assert.Equal(element, slave.Memory.AsSpan(0x3000, 4).ToArray());
    }
}
