using System.Text;
using PeakCan.Host.Core.Xcp.Record;
using PeakCan.Host.Core.Xcp.Receive;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Xcp.Record;

/// <summary>
/// S4-T4：invalidation bits + 归因事件组（spec D3/Q1）。
/// 判据 3：制造 MissingCause 空窗 + PlanGap → 失效位置位 + 事件条目时间区间可对上。
/// </summary>
public sealed class XcpMdfGapRecordTests
{
    private static readonly MdfChannelSpec Rpm = new("EngineSpeed", "rpm");
    private static readonly MdfChannelSpec Speed = new("VehicleSpeed", "km/h");

    private static string TempPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s4g_{Guid.NewGuid():N}.mf4");

    private static string TempDir() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s4g_{Guid.NewGuid():N}");

    private static PlannedDaqEntry Entry(int index, string name) =>
        new(2, 1, (ushort)index, name, 0, 2, 0, 0x2000, null);

    // ---------------- writer 层 ----------------

    [Fact]
    public async Task Invalid_row_sets_invalidation_bit_and_keeps_master_valid()
    {
        var path = TempPath();
        try
        {
            var writer = Mdf4StreamWriter.Create(path, [Rpm], DateTimeOffset.UtcNow);
            await writer.WriteRecordAsync(0, 0.0, 1.0);
            await writer.WriteInvalidRecordAsync(0, 0.5);
            await writer.WriteRecordAsync(0, 1.0, 3.0);
            await writer.FinalizeAsync();
            await writer.DisposeAsync();

            var layout = Mdf4GapLayout.Parse(await File.ReadAllBytesAsync(path));
            var dg = layout.SampleGroups[0];
            Assert.Equal(3, dg.CycleCount);
            Assert.Equal(17, dg.RecordSizeBytes);
            Assert.Equal(1, dg.InvalBytesNr);
            Assert.Equal(0.0, dg.Records[0].Time);
            Assert.Equal(1.0, dg.Records[0].Value);
            Assert.False(dg.Records[0].Invalid);
            Assert.Equal(0.5, dg.Records[1].Time);
            Assert.True(dg.Records[1].Invalid);          // 空窗行失效
            Assert.False(dg.Records[2].Invalid);
            Assert.Equal(3.0, dg.Records[2].Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Gap_events_land_in_event_group_with_sd_strings()
    {
        var path = TempPath();
        try
        {
            var writer = Mdf4StreamWriter.Create(path, [Rpm], DateTimeOffset.UtcNow);
            await writer.WriteGapEventAsync(1.5, "MissingCauseAttributed",
                "ConversionUnsupported", "cvt MAP2 unsupported", "MalformedFrame", 0.0);
            await writer.WriteGapEventAsync(3.0, "PlanGapOpened", "", "plan gap", "", 0.25);
            await writer.FinalizeAsync();
            await writer.DisposeAsync();

            var layout = Mdf4GapLayout.Parse(await File.ReadAllBytesAsync(path));
            Assert.Single(layout.SampleGroups); // 1 样本 DG（事件组单列 EventGroup）
            var ev = layout.EventGroup!;
            Assert.Equal([1.5, 3.0], ev.Times);
            Assert.Equal(["MissingCauseAttributed", "PlanGapOpened"], ev.Kinds);
            Assert.Equal(["ConversionUnsupported", ""], ev.Causes);
            Assert.Equal(["cvt MAP2 unsupported", "plan gap"], ev.Details);
            Assert.Equal(["MalformedFrame", ""], ev.ReceiveKinds);
            Assert.Equal([0.0, 0.25], ev.ExpectedMaxDurations);
            Assert.Equal(0, layout.SampleGroups[0].CycleCount); // 样本 DG 未动
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------- sink 层：判据 3 ----------------

    [Fact]
    public async Task Missing_cause_gap_then_plan_gap_marks_invalidation_and_events_align()
    {
        var dir = TempDir();
        try
        {
            var sink = new XcpMdfRecordSink(new XcpMdfRecordSinkOptions
            {
                Directory = dir,
                Channels = [Rpm, Speed],
            });
            var t0 = DateTimeOffset.UtcNow;
            await sink.StartAsync(t0);
            sink.OnValues(new XcpDaqSample(Entry(0, "EngineSpeed"), 1.0, t0));
            // 空窗：逐帧归因（MissingCause）→ 计划空窗开窗 → 断流升级。
            sink.OnGap(XcpAcquisitionGap.FromAttribution(new XcpReceiveAttribution(
                XcpReceiveAttributionKind.DecodeFailed, 0, "decode failed",
                A2lEditor.Core.Layout.MissingCause.ConversionUnsupported)));
            sink.OnGap(XcpAcquisitionGap.PlanGapOpened(new PlanGapWindow(
                OdtCount: 1, EntryCount: 1, ExpectedMaxDuration: TimeSpan.FromMilliseconds(250))));
            sink.OnGap(XcpAcquisitionGap.AcquisitionInterrupted("plan gap timeout"));
            await sink.StopAsync();

            var layout = Mdf4GapLayout.Parse(await File.ReadAllBytesAsync(sink.FilePath!));

            // 3 条事件全落（逐帧归因/PlanGap/断流），kind 字符串可辨。
            var ev = layout.EventGroup!;
            Assert.Equal(3, ev.Times.Count);
            Assert.Equal("MissingCauseAttributed", ev.Kinds[0]);
            Assert.Equal("PlanGapOpened", ev.Kinds[1]);
            Assert.Equal("AcquisitionInterrupted", ev.Kinds[2]);
            Assert.Equal("ConversionUnsupported", ev.Causes[0]);
            Assert.Equal("AcquisitionInterrupted", ev.Causes[2]);
            Assert.Equal(0.25, ev.ExpectedMaxDurations[1]);

            // 失效位：两个样本 DG 各 3 条空窗行（每条 gap 一行），时间与事件条目可对上。
            foreach (var dg in layout.SampleGroups)
            {
                Assert.Equal(3, dg.Records.Count(r => r.Invalid));
                var invalidTimes = dg.Records.Where(r => r.Invalid).Select(r => r.Time).ToList();
                Assert.Equal(ev.Times.Select(t => t).ToList(), invalidTimes);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task No_gap_means_no_invalidation_and_no_events()
    {
        var dir = TempDir();
        try
        {
            var sink = new XcpMdfRecordSink(new XcpMdfRecordSinkOptions
            {
                Directory = dir,
                Channels = [Rpm],
            });
            await sink.StartAsync(DateTimeOffset.UtcNow);
            sink.OnValues(new XcpDaqSample(Entry(0, "EngineSpeed"), 1.0, DateTimeOffset.UtcNow));
            await sink.StopAsync();

            var layout = Mdf4GapLayout.Parse(await File.ReadAllBytesAsync(sink.FilePath!));
            var dg = layout.SampleGroups[0];
            Assert.Equal(1, dg.CycleCount);
            Assert.All(dg.Records, r => Assert.False(r.Invalid)); // host 两态不落盘：无失效行
            Assert.NotNull(layout.EventGroup);
            Assert.Empty(layout.EventGroup!.Times);               // 无归因事件
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}


/// <summary>测试侧 MDF4 解析（T4 面）：样本 DG 失效位 + 事件 DG 的 SD 字符串。</summary>
internal sealed record GapGroupInfo(
    long CycleCount, int RecordSizeBytes, int InvalBytesNr,
    IReadOnlyList<(double Time, double Value, bool Invalid)> Records);

internal sealed record GapEventInfo(
    IReadOnlyList<double> Times,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> Causes,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> ReceiveKinds,
    IReadOnlyList<double> ExpectedMaxDurations);

internal sealed record Mdf4GapLayout(
    IReadOnlyList<GapGroupInfo> SampleGroups,
    GapEventInfo? EventGroup)
{
    public static Mdf4GapLayout Parse(byte[] b)
    {
        ulong U64(int o) => BitConverter.ToUInt64(b, o);
        uint U32(int o) => BitConverter.ToUInt32(b, o);

        var dg = U64(0x58); // HD.first_dg
        var sampleGroups = new List<GapGroupInfo>();
        GapEventInfo? eventGroup = null;

        while (dg != 0)
        {
            Assert.Equal("##DG", Encoding.ASCII.GetString(b, (int)dg, 4));
            var cg = U64((int)dg + 32);
            var dl = U64((int)dg + 40);
            var cycles = (long)U64((int)cg + 80);
            var invalNr = (int)U32((int)cg + 100); // cg_inval_bytes_nr @+100（asammdf 权威口径）
            var sampleNr = (int)U32((int)cg + 96);
            var stride = sampleNr + invalNr;

            // 事件 DG 判别：CG 记录 32B（time+expected+4×u32 SD 偏移），无失效位。
            var isEvent = sampleNr == 32 && invalNr == 0;

            if (isEvent)
            {
                // 事件 CN 链：time → kind → cause → detail → receive_kind → expected。
                var timeCn = U64((int)cg + 32);
                var kindCn = U64((int)timeCn + 24);
                var causeCn = U64((int)kindCn + 24);
                var detailCn = U64((int)causeCn + 24);
                var recvCn = U64((int)detailCn + 24);
                var expCn = U64((int)recvCn + 24);

                var times = new List<double>();
                var kinds = new List<string>();
                var causes = new List<string>();
                var details = new List<string>();
                var recv = new List<string>();
                var expected = new List<double>();

                // DT 记录直接解：time@0, expected@8, kind@16, cause@20, detail@24, recv@28（SD 偏移）。
                var dtRecords = ReadRawRecords(b, dl, stride);
                var offKind = new List<int>();
                var offCause = new List<int>();
                var offDetail = new List<int>();
                var offRecv = new List<int>();
                foreach (var rec in dtRecords)
                {
                    times.Add(BitConverter.ToDouble(rec, 0));
                    expected.Add(BitConverter.ToDouble(rec, 8));
                    offKind.Add(BitConverter.ToInt32(rec, 16));
                    offCause.Add(BitConverter.ToInt32(rec, 20));
                    offDetail.Add(BitConverter.ToInt32(rec, 24));
                    offRecv.Add(BitConverter.ToInt32(rec, 28));
                }

                var sdKind = U64((int)kindCn + 64);
                var sdCause = U64((int)causeCn + 64);
                var sdDetail = U64((int)detailCn + 64);
                var sdRecv = U64((int)recvCn + 64);
                kinds.AddRange(ReadSdStrings(b, sdKind, offKind));
                causes.AddRange(ReadSdStrings(b, sdCause, offCause));
                details.AddRange(ReadSdStrings(b, sdDetail, offDetail));
                recv.AddRange(ReadSdStrings(b, sdRecv, offRecv));

                eventGroup = new GapEventInfo(times, kinds, causes, details, recv, expected);
            }
            else
            {
                var records = new List<(double, double, bool)>();
                foreach (var rec in ReadRawRecords(b, dl, stride))
                {
                    var time = BitConverter.ToDouble(rec, 0);
                    var value = BitConverter.ToDouble(rec, 8);
                    var invalid = invalNr > 0 && (rec[sampleNr] & 0x01) != 0;
                    records.Add((time, value, invalid));
                }
                sampleGroups.Add(new GapGroupInfo(cycles, stride, invalNr, records));
            }

            dg = U64((int)dg + 24); // dg.next
        }

        return new Mdf4GapLayout(sampleGroups, eventGroup);
    }

    private static List<byte[]> ReadRawRecords(byte[] b, ulong dl, int stride)
    {
        var result = new List<byte[]>();
        var dlOff = (int)dl;
        Assert.Equal("##DL", Encoding.ASCII.GetString(b, dlOff, 4));
        var count = (int)U32At(b, dlOff + 24 + (1 + 1024) * 8 + 4); // DL count（Finalize 回写）
        for (var slot = 0; slot < count; slot++)
        {
            var dt = BitConverter.ToUInt64(b, dlOff + 24 + 8 + 8 * slot);
            if (dt == 0)
                continue;
            Assert.Equal("##DT", Encoding.ASCII.GetString(b, (int)dt, 4));
            var dtLen = (long)BitConverter.ToUInt64(b, (int)dt + 8);
            for (var off = (int)dt + 24; off + stride <= (int)dt + dtLen; off += stride)
                result.Add(b[off..(off + stride)]);
        }
        return result;

        static uint U32At(byte[] b, int o) => BitConverter.ToUInt32(b, o);
    }

    /// <summary>SD 块：记录偏移指向 [u32 len][utf8] 条目。</summary>
    private static List<string> ReadSdStrings(byte[] b, ulong sd, List<int> offsets)
    {
        var result = new List<string>();
        if (sd == 0)
            return result;
        Assert.Equal("##SD", Encoding.ASCII.GetString(b, (int)sd, 4));
        var baseOff = (int)sd + 24;
        foreach (var off in offsets)
        {
            var len = BitConverter.ToInt32(b, baseOff + off);
            result.Add(Encoding.UTF8.GetString(b, baseOff + off + 4, len));
        }
        return result;
    }
}
