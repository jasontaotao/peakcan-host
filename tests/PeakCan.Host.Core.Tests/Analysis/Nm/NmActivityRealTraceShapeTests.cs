using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.5.5 验收表：真实 trace 的切段必须落在实测的 NMSts 变化点上。
/// <para>
/// 用与 <see cref="NmRealTraceContractTests"/> 同一份合成 fixture（形态与真实 trace
/// 一致：两段 RMS + 各自的 NOS 尾），但断言的是**时间条切段**而不是 finding。
/// 两者是不同层：finding 说「RMS 时长超差」，时间条说「哪一段是 repeat、哪一段是 normal」。
/// </para>
/// </summary>
public class NmActivityRealTraceShapeTests
{
    private static readonly string[] CannmReasons = ["MsgType=CANNM"];

    private static ReplayFrame FrameAt(double t, uint id, byte nid, byte nmsts)
        => new(t, id, 8, new byte[] { nid, 0x00, 0x00, nmsts, 0, 0, 0, 0 },
            FrameFlags.None, IsExtended: true);

    /// <summary>与 §7.5.5 验收表同形的两段：repeat → normal，repeat（含快发）→ normal。</summary>
    private static ReplayFrame[] RealTraceShape()
    {
        double[] rms1 = [0.0, 0.525, 1.05, 1.575, 2.1];
        double[] nos1 = [2.6, 3.114, 3.651, 4.188];
        double[] rms2Burst = [6.5, 6.520, 6.540, 6.560, 6.580, 6.600, 6.620, 6.640, 6.660, 6.680];
        double[] rms2 = [7.18, 7.71, 8.24, 8.775];
        double[] nos2 = [9.275, 9.789];

        var frames = new List<ReplayFrame>();
        Add(frames, rms1, 0); Add(frames, nos1, 1);
        Add(frames, rms2Burst, 0); Add(frames, rms2, 0); Add(frames, nos2, 1);
        return frames.ToArray();

        static void Add(List<ReplayFrame> target, double[] times, byte nmsts)
        {
            foreach (var t in times)
            {
                target.Add(FrameAt(t, 0x18FFFAF4, 0xF4, nmsts));
                target.Add(FrameAt(t, 0x18FFFAF5, 0xF5, nmsts));
            }
        }
    }

    /// <summary>绑定吉利 profile 的 NMSts 位角色（byte3.bit0）。</summary>
    private static NmProfile GeelyProfile()
    {
        var def = new NmMessageDef("0x18FFFAF4", true, "NM_F4", "A", 8, true,
            CannmReasons, 500, 2000, true);
        return new NmProfile(
            NmProfileSource.Dbc,
            new[] { def },
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(),
            Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(),
            new NmProvenance(),
            StandardBitRoles: new[] { new NmBitRole(3, 0, "NmSts") },
            StandardTimings: new NmStandardTimings
            {
                CycleMs = 500, TimeoutMs = 2000,
                RepeatMessageWithBurstMs = 1700,
                RepeatMessageWithoutBurstMs = 1500,
                ImmediateCycleMs = 20, ImmediateCount = 10,
            });
    }

    [Fact]
    public void Analyze_RealTraceShape_SegmentsFollowNmStsBoundaries()
    {
        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "geely-shape", null, RealTraceShape(), null, GeelyProfile(), 0, 10.0));

        var message = Assert.Single(report.Messages, m => m.CanIdHex == "0x18FFFAF4");
        var segments = message.Activity.OrderBy(s => s.Start).ToArray();

        // 有 NMSts 时不得再画 wakeup（spec §7.5.1 第 4 条）。
        Assert.DoesNotContain(segments, s => s.Kind == "wakeup");

        // 两段 repeat。终点取**下一次变化帧**（spec §7.5.1 原文），
        // 不是最后一个同状态帧——真实 trace 的 2.215708 也正是 NMSts 变 1 的那一帧。
        // 因此第一段 repeat 是 0.0–2.6（nos1 首帧），第二段是 6.5–9.275。
        var repeats = segments.Where(s => s.Kind == "repeat").ToArray();
        Assert.Equal(2, repeats.Length);
        Assert.Equal(0.0, repeats[0].Start, precision: 6);
        Assert.Equal(2.6, repeats[0].End, precision: 6);
        // 第二段含快发，快发部分被切成独立的 immediate 子段，
        // 所以 repeat 从快发末帧 6.68 开始（§7.5.1.1 要求快发可见）。
        Assert.Equal(6.68, repeats[1].Start, precision: 6);
        Assert.Equal(9.275, repeats[1].End, precision: 6);
        Assert.All(repeats, s => Assert.Equal("measured", s.Evidence));

        // 第二段内部必须切出快发子段（spec §7.5.1.1：NM_03/NM_04 的证据）。
        var immediate = Assert.Single(segments, s => s.Kind == "immediate");
        Assert.Equal(6.5, immediate.Start, precision: 6);
        Assert.Equal(6.68, immediate.End, precision: 6);

        // 两段 normal：2.6–4.188 与 9.275–9.789（NMSts=1）。
        var normals = segments.Where(s => s.Kind == "normal").ToArray();
        Assert.Equal(2, normals.Length);
        Assert.Equal(2.6, normals[0].Start, precision: 6);
        Assert.Equal(4.188, normals[0].End, precision: 6);
        Assert.Equal(9.275, normals[1].Start, precision: 6);
        Assert.Equal(9.789, normals[1].End, precision: 6);
    }

    [Fact]
    public void Analyze_NoNmStsRole_KeepsM1TimeInference()
    {
        // 同一份 trace，但 profile 不声明 NMSts → 必须退回 M1 的 wakeup/active，
        // 不得凭空画 repeat/normal（spec §7.5.5 的 M1 回归要求）。
        var profile = GeelyProfile() with { StandardBitRoles = null };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "no-sts", null, RealTraceShape(), null, profile, 0, 10.0));

        var message = Assert.Single(report.Messages, m => m.CanIdHex == "0x18FFFAF4");
        Assert.Contains(message.Activity, s => s.Kind == "wakeup");
        Assert.DoesNotContain(message.Activity, s => s.Kind is "repeat" or "normal");
        Assert.All(message.Activity, s => Assert.Equal("inferred", s.Evidence));
    }
}
