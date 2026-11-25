using System.Security.Cryptography;
using System.Text;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmReasonRealTraceTests
{
    private const string DbcSha =
        "23d121d65b103a1a4ea5cde027b6124649e23a92d52d55715e6f4a8e4ae7d43f";

    static NmReasonRealTraceTests()
        => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static string DataDir()
        => System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "Nm");

    private static (DbcDocument Dbc, IReadOnlyList<ReplayFrame> Frames) Load(bool keepVal)
    {
        var dbcBytes = File.ReadAllBytes(System.IO.Path.Combine(DataDir(), "bms.dbc"));
        // 解释必须钉在权威 DBC 上：换文件后结论可能变义，测试要先发现。
        Assert.Equal(DbcSha, Convert.ToHexString(SHA256.HashData(dbcBytes)).ToLowerInvariant());

        var text = Encoding.GetEncoding("GBK").GetString(dbcBytes);
        if (!keepVal)
        {
            var lines = text.Split('\n')
                .Where(l => !l.StartsWith("VAL_ 2566912756 BMS_Wakeup_reasons", StringComparison.Ordinal)
                         && !l.StartsWith("VAL_ 2566912756 BMS_Stayawake_reasons", StringComparison.Ordinal));
            text = string.Join('\n', lines);
        }
        var dbc = DbcParser.Parse(text).Value!;

        using var fs = File.OpenRead(System.IO.Path.Combine(DataDir(), "nm16.blf"));
        var frames = BlfParser.ParseAsync(fs, ReplayOptions.Default)
            .GetAwaiter().GetResult();
        return (dbc, frames);
    }

    private static NmReport Analyze(bool keepVal)
    {
        var (dbc, frames) = Load(keepVal);
        var profile = NmProfileBuilder.Build(dbc, null);
        var loaded = new LoadedNmReasonDictionary(
            new NmReasonDictionary(1,
            [
                new NmReasonFieldDef("BMS_Wakeup_reasons", "0x18FFFAF4", true),
                new NmReasonFieldDef("BMS_Stayawake_reasons", "0x18FFFAF4", true),
            ]),
            "mem.nmreasons.json", "hash");
        return NmAnalyzer.Analyze(new NmAnalysisRequest(
            "real", null, frames, dbc, profile, 0, frames[^1].Timestamp + 1e-9,
            "full", Reasons: loaded));
    }

    [Fact]
    public void RealTrace_WakeupAndStayawakeSegmentsMatchProbe()
    {
        var report = Analyze(keepVal: true);
        var wakeup = report.ReasonTimelines.Single(t => t.Signal == "BMS_Wakeup_reasons");
        var stay = report.ReasonTimelines.Single(t => t.Signal == "BMS_Stayawake_reasons");

        Assert.Collection(wakeup.Segments,
            s =>
            {
                Assert.Equal((0.095, 0.095),
                    (Math.Round(s.Start, 3), Math.Round(s.End, 3)));
                Assert.Equal(0UL, s.RawValue);
                Assert.Equal(0.609, s.NextObservedAt!.Value, 3);
                Assert.Equal(NmReasonSegmentEnd.Changed, s.EndReason);
            },
            s =>
            {
                Assert.Equal(0.609, Math.Round(s.Start, 3));
                Assert.Equal(9.714, Math.Round(s.End, 3));
                Assert.Equal(48UL, s.RawValue);
                Assert.Equal(NmReasonSegmentEnd.EpisodeEnd, s.EndReason);
                Assert.Equal(12.677, s.NextObservedAt!.Value, 3);
                Assert.Contains(s.Labels,
                    l => l.Text == "Waked up by NW PDU" && l.Source == "dbc");
            },
            s =>
            {
                Assert.Equal(12.677, Math.Round(s.Start, 3));
                Assert.Equal(30.536, Math.Round(s.End, 3));
                Assert.Equal(NmReasonSegmentEnd.WindowEnd, s.EndReason);
                Assert.True(s.EndUnknown);
            });

        Assert.Collection(stay.Segments,
            s =>
            {
                Assert.Equal(0.095, Math.Round(s.Start, 3));
                Assert.Equal(9.714, Math.Round(s.End, 3));
                Assert.Equal(1UL, s.RawValue);
                Assert.Equal(NmReasonSegmentEnd.EpisodeEnd, s.EndReason);
            },
            s =>
            {
                Assert.Equal(12.677, Math.Round(s.Start, 3));
                Assert.Equal(19.826, Math.Round(s.End, 3));
                Assert.Equal(2UL, s.RawValue);
                Assert.Equal(NmReasonSegmentEnd.Changed, s.EndReason);
                Assert.Equal(20.363, s.NextObservedAt!.Value, 3);
            },
            s =>
            {
                Assert.Equal(20.363, Math.Round(s.Start, 3));
                Assert.Equal(30.536, Math.Round(s.End, 3));
                Assert.Equal(1UL, s.RawValue);
                Assert.Equal(NmReasonSegmentEnd.WindowEnd, s.EndReason);
            });
    }

    [Fact]
    public void RealTrace_WithoutValueTables_LabelsAreEmpty()
    {
        var report = Analyze(keepVal: false);
        var wakeup = report.ReasonTimelines.Single(t => t.Signal == "BMS_Wakeup_reasons");
        Assert.All(wakeup.Segments, s => Assert.Empty(s.Labels));
        Assert.Contains(wakeup.Segments, s => s.RawValue == 48UL);
    }
}
