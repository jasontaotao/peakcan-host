using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// NM M2 S2 验收（spec §5.2 / §11）：统一时序接入后，
/// ① 无 profile 时行为与 M1 完全一致；② 吉利 profile 收紧容差生效。
/// </summary>
public class NmTimingMigrationTests
{
    [Fact]
    public void CycleAnalyzer_NoToleranceParam_KeepsM1Behavior()
    {
        // M1 容差 = max(0.2×500, 30) = 100ms。偏差 80ms 不超容差 → 不算偏差。
        var timestamps = BuildTimestamps(cycleMs: 500, count: 12, offsetMs: 80);

        var stats = NmCycleAnalyzer.Analyze(timestamps, Def(500), Array.Empty<NmEpisode>());

        Assert.Equal(0, stats.DeviationCount);
        Assert.Equal(100, stats.ToleranceMs);
    }

    [Fact]
    public void CycleAnalyzer_GeelyTolerance_TighterAndReportsDeviation()
    {
        // 吉利 ±10% = 50ms。同一组偏差 80ms 现在超容差 → 应报偏差。
        var timestamps = BuildTimestamps(cycleMs: 500, count: 12, offsetMs: 80);
        var geely = NmTimingDefaults.FromProfile(null, new NmStandardTimings
        {
            CycleMs = 500,
            TimeoutMs = 2000,
            ToleranceRatio = 0.1,
        });

        var stats = NmCycleAnalyzer.Analyze(
            timestamps, Def(500), Array.Empty<NmEpisode>(),
            toleranceMs: geely.ToleranceMsFor(500));

        Assert.Equal(50, stats.ToleranceMs);
        Assert.True(stats.DeviationCount > 0,
            "80ms 偏差在 ±10%（50ms）下必须算偏差，否则收紧容差没生效");
    }

    [Fact]
    public void ToleranceMsFor_ShortCycle_KeepsAbsFloor()
    {
        // 短周期下只按比例会过严：100ms × 0.2 = 20ms < 30ms 下限。
        var m1 = NmTimingDefaults.FromProfile(null, null);

        Assert.Equal(30, m1.ToleranceMsFor(100));
        Assert.Equal(100, m1.ToleranceMsFor(500));
    }

    [Fact]
    public void FromProfile_StandardTimingsOnly_NoM1_AppliesGeelyValues()
    {
        var geely = NmTimingDefaults.FromProfile(null, new NmStandardTimings
        {
            CycleMs = 500,
            TimeoutMs = 2200,
            ToleranceRatio = 0.1,
        });

        Assert.Equal(2200, geely.TimeoutMs);
        Assert.Equal(0.1, geely.ToleranceRatio);
        // 未配下限时保留 M1 的 30ms。
        Assert.Equal(30, geely.ToleranceAbsMs);
    }

    [Fact]
    public void FromProfile_M1Timings_TakePrecedenceOverStandard()
    {
        // 项目层（DBC/sidecar）> 企标：DBC 写 1000ms 不能被企标的 2200 盖掉。
        var timing = NmTimingDefaults.FromProfile(
            new NmTimings(CycleMs: 500, TimeoutMs: 1000),
            new NmStandardTimings { TimeoutMs = 2200 });

        Assert.Equal(1000, timing.TimeoutMs);
    }

    [Fact]
    public void Analyze_M1TimeoutNot2000_EpisodeStillSplits()
    {
        // 回归：DBC/sidecar 把超时配成 1000ms 时，1.2s 静默必须切成两个 episode。
        // 修复前 FromProfile 丢弃 M1 Timings，阈值退化成 2000ms → 不切分。
        var profile = new NmProfile(
            NmProfileSource.Dbc,
            new[] { Def(500) },
            new NmTimings(CycleMs: 500, TimeoutMs: 1000),
            new[] { new NmNodeDef("A", 0xF4, null, true) },
            Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(),
            new NmProvenance());

        var frames = new[]
        {
            Frame(0.0), Frame(0.5), Frame(1.0),
            Frame(2.2), Frame(2.7), Frame(3.2),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 3.5));

        Assert.Equal(2, report.Episodes.Count);
    }

    [Fact]
    public void Analyze_StandardTimingTightensTolerance_ReportsDeviation()
    {
        // 端到端：经过 NmAnalyzer 装配后，吉利 ±10% 容差必须让 80ms 偏差成为 finding。
        // 若把 NmAnalyzer 的 toleranceMs 传参回退成 null，本测试必须变红。
        var profile = new NmProfile(
            NmProfileSource.Dbc,
            new[] { Def(500) },
            new NmTimings(),
            new[] { new NmNodeDef("A", 0xF4, null, true) },
            Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(),
            new NmProvenance(),
            StandardTimings: new NmStandardTimings
            {
                CycleMs = 500,
                TimeoutMs = 2000,
                ToleranceRatio = 0.1,
            });

        // 12 帧、每帧 580ms（偏差 +80ms > ±10% 的 50ms）。
        var frames = Enumerable.Range(0, 12)
            .Select(i => Frame(i * 0.58))
            .ToArray();

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 7.0));

        Assert.Contains(report.Findings, f => f.Code == "NM_CYCLE_DEVIATION");
    }

    [Fact]
    public void Analyze_NoProfile_NullCycleToleranceStaysNull()
    {
        // 回归：报文没有周期时 ToleranceMs 必须是 null，不能因为传了容差变成 100。
        var profile = new NmProfile(
            NmProfileSource.Dbc,
            new[]
            {
                new NmMessageDef("0x500", false, "NM", "A", 8, true,
                    TestReasons, CycleMs: null, TimeoutMs: 2000),
            },
            new NmTimings(),
            new[] { new NmNodeDef("A", 0xF4, null, true) },
            Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(),
            new NmProvenance());

        var frames = Enumerable.Range(0, 8).Select(i => Frame(i * 0.5)).ToArray();

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 4.0));

        var message = Assert.Single(report.Messages);
        Assert.Null(message.Cycle.ToleranceMs);
    }

    private static PeakCan.Host.Core.Replay.ReplayFrame Frame(double t)
        => new(t, 0x500, 8, new byte[] { 0xF4, 0, 0, 0, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None);

    private static NmMessageDef Def(int cycleMs)
        => new("0x500", false, "NM", "A", 8, true, TestReasons, CycleMs: cycleMs, TimeoutMs: 2000);

    private static readonly string[] TestReasons = ["test"];

    private static double[] BuildTimestamps(int cycleMs, int count, int offsetMs)
    {
        var result = new double[count];
        var t = 0.0;
        result[0] = t;
        for (var i = 1; i < count; i++)
        {
            t += (cycleMs + offsetMs) / 1000.0;
            result[i] = t;
        }
        return result;
    }
}
