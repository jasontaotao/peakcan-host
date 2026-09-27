using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Bench;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Protocol;
using Xunit;

namespace PeakCan.Host.Core.Tests.Xcp.Bench;

/// <summary>S7-T5：C 系场景——C-1 广播写 + C-2 静态跨段扫描 + C-3 MAP 上传计时。</summary>
public sealed class CScenariosTests
{
    // ---------------- C-2 静态扫描 ----------------

    private static readonly string[] TailFlags = ["-1", "-1", "-1", "-1", "-1"];

    private static (A2lDocument Doc, ContractSet Contracts) MakeValBlkDoc(
        uint secondMappingPhysical = 0x3000, bool gapAfterFirst = false)
    {
        var rl = new A2lRecordLayout("RL_F32",
            new[] { new RecordLayoutEntry("FNC_VALUES", 0, "FLOAT32_IEEE", "COLUMN_SCAL", "DIRECT", null, null) },
            new LineRange(1, 1));
        var ch = new A2lCharacteristic("KmMap", "d", "VAL_BLK", "RL_F32", 0x1500,
            "0", "100", null, "CM_ID", new LineRange(1, 1), MatrixDim: new uint[] { 2 });
        var module = new A2lModule("M", "m",
            Array.Empty<A2lMeasurement>(), new[] { ch }, Array.Empty<A2lAxisPts>(),
            new[] { new A2lCompuMethod("CM_ID", "id", "IDENTICAL", "%.2f", "unit",
                new IdenticalConversion(), new LineRange(10, 10)) },
            new[] { rl }, Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1),
            MemorySegments: new[]
            {
                MakeSegment("SEG_A", 0x1500, 0x1500, 4),
                gapAfterFirst
                    ? MakeSegmentNoMapping("SEG_B", 0x1504, 0x3000, 4)
                    : MakeSegment("SEG_B", 0x1504, secondMappingPhysical, 4),
            });
        var doc = new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
        return (doc, new ContractSet(doc));
    }

    private static A2lMemorySegment MakeSegment(
        string name, uint logical, ulong physical, uint length)
    {
        var mappings = new[] { new XcpAddressMapping(logical, physical, length,
            Array.Empty<XcpMissingField>(), string.Empty) };
        var segment = new XcpSegment(0, 2, 0, null, null, mappings, 1,
            Array.Empty<XcpMissingField>(), string.Empty);
        return WrapIfData(segment, name, logical, length);
    }

    private static A2lMemorySegment MakeSegmentNoMapping(
        string name, uint logical, ulong physical, uint length)
    {
        var segment = new XcpSegment(0, 2, 0, null, null,
            Array.Empty<XcpAddressMapping>(), 1,
            Array.Empty<XcpMissingField>(), string.Empty);
        return WrapIfData(segment, name, logical, length);
    }

    private static A2lMemorySegment WrapIfData(XcpSegment segment, string name, uint logical, uint length)
    {
        var ifData = new XcpIfData(XcpIfDataScope.MemorySegmentLevel, null, null, null, null,
            Array.Empty<XcpOnCan>(), new[] { segment },
            Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), string.Empty);
        return new A2lMemorySegment(name, name, "DATA", "FLASH", "INTERN",
            logical, length, TailFlags, new LineRange(1, 1), ifData);
    }

    [Fact]
    public void Scan_single_mapping_reports_no_cross_segment()
    {
        var (doc, contracts) = BenchDocs.MakeScalarDoc(0x1500);
        var report = CrossSegmentScanner.Scan(doc, contracts);

        Assert.Equal(1, report.ObjectsScanned);
        Assert.Equal(1, report.SingleRunObjects);
        Assert.False(report.HasCrossSegmentObjects);
        Assert.Equal(0, report.UnmappedObjects);
    }

    [Fact]
    public void Scan_cross_mapping_object_is_detected_with_run_count()
    {
        var (doc, contracts) = MakeValBlkDoc();
        var report = CrossSegmentScanner.Scan(doc, contracts);

        Assert.Equal(1, report.ObjectsScanned);
        Assert.True(report.HasCrossSegmentObjects);
        var finding = Assert.Single(report.Findings, f => f.RunCount > 1);
        Assert.Equal("KmMap", finding.ObjectName);
        Assert.Equal(2, finding.RunCount);
    }

    [Fact]
    public void Scan_mapping_gap_is_recorded_as_unmapped()
    {
        var (doc, contracts) = MakeValBlkDoc(gapAfterFirst: true);
        var report = CrossSegmentScanner.Scan(doc, contracts);

        Assert.Equal(1, report.UnmappedObjects);
        Assert.Contains(report.Findings, f => f.RunCount == 0);
    }

    // ---------------- C-1 广播写 ----------------

    [Fact]
    public async Task Broadcast_write_covers_all_elements_and_restores()
    {
        var (doc, contracts) = MakeValBlkDoc();
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

        // 播种两个元素：0x1500=1.0f、0x3000=2.0f（跨段物理地址）。
        BitConverter.GetBytes(1.0f).CopyTo(slave.Memory, 0x1500);
        BitConverter.GetBytes(2.0f).CopyTo(slave.Memory, 0x3000);

        var contract = contracts.All.First(c => c.ObjectName == "KmMap");
        var result = await BroadcastWriteScenario.RunAsync(
            writer, master, contract, doc, 9.0f, "c1_broadcast");

        // C-1 事实：2 元素广播。
        Assert.Equal(2, result.ElementCount);
        Assert.True(result.Rmr.WriteVerified);
        Assert.True(result.Rmr.Restored);

        // 还原后两段各自回到原值（广播写 + 逐段还原都到达了跨段物理地址）。
        Assert.Equal(1.0f, BitConverter.ToSingle(slave.Memory, 0x1500), 3);
        Assert.Equal(2.0f, BitConverter.ToSingle(slave.Memory, 0x3000), 3);
    }

    // ---------------- C-3 MAP 上传计时 ----------------

    [Fact]
    public async Task Map_upload_timing_reports_grid_and_elapsed()
    {
        // 复用 S6 真机 fixture 模式：App_merge_INCA.a2l 找一个可在线读的 MAP。
        var (contracts, mapName) = Fixture.Value;
        var slave = new MemorySlaveTransport();
        var spy = new XcpTransportSpy(slave);
        var master = new XcpMaster(spy, new XcpMasterOptions(
            new CanId(0x18FFF667, FrameFormat.Extended), TimeSpan.FromMilliseconds(100), 0));

        var result = await MapUploadTimingScenario.RunAsync(
            master, contracts, mapName);

        Assert.Equal(mapName, result.ObjectName);
        Assert.True(result.XCount > 0);
        Assert.True(result.YCount > 0);
        Assert.True(result.Elapsed > TimeSpan.Zero);
        // 只读口径：零 DOWNLOAD（S6-T4 钉延续）。
        Assert.Equal(0, spy.Sent.Count(f => f.Data.Span[0] == XcpPid.Download));
    }

    private static readonly Lazy<(ContractSet Contracts, string MapName)> Fixture = new(() =>
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l");
        var parsed = Asap2PackageApi.ParseFile(path);
        Assert.NotNull(parsed.Value);
        var contracts = Asap2PackageApi.Contracts(parsed.Value!);
        var doc = contracts.Document;

        foreach (var map in doc.Modules.SelectMany(m => m.Characteristics))
        {
            if (map.Type != "MAP" || map.EcuAddress is null || map.AxisDescrs.Count != 2)
                continue;
            if (map.AxisDescrs.Any(d => d.AxisPtsRef is null))
                continue;
            if (!contracts.TryGet(map.Name, out var mc) || mc.DataType is null)
                continue;

            var axes = map.AxisDescrs
                .Select(d => doc.Modules.SelectMany(m => m.AxisPts).FirstOrDefault(a => a.Name == d.AxisPtsRef))
                .ToArray();
            if (axes.Any(a => a is null || a.EcuAddress is null || a.NumberOfAxisPts is null))
                continue;

            var addrs = new[] { map.EcuAddress.Value, axes[0]!.EcuAddress!.Value, axes[1]!.EcuAddress!.Value }
                .Select(a => a & 0xFFFF).ToArray();
            if (addrs.Distinct().Count() != 3)
                continue;
            var maxLen = (long)mc.TotalByteLength +
                axes.Max(a => (long)a!.NumberOfAxisPts!.Value * 8);
            if ((long)addrs.Min() + maxLen > 0x10000)
                continue;

            return (contracts, map.Name);
        }
        throw new InvalidOperationException("fixture 找不到可测的真机 MAP 对象");
    });
}
