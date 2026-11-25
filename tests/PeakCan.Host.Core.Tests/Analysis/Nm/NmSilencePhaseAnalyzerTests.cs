using System.Globalization;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// 静默相位推断（spec `2026-11-25-nm-silence-phase-design.md`）。
/// 全部相位必须是 inferred；判据是「超时边界 + 发送者归属 + 总线是否静止」。
/// </summary>
public class NmSilencePhaseAnalyzerTests
{
    // CA1861: 常量数组不内联，避免每次调用都重新分配。
    private static readonly string[] SingleParticipant = ["0x500"];

    private static readonly NmMessageDef NmDef = new(
        CanIdHex: "0x18FFFAF4", IsExtended: true, Name: "BMS_NWM", Sender: "BMS",
        Dlc: 8, IsNm: true, Reasons: Array.Empty<string>(), CycleMs: 500, TimeoutMs: 2000);

    private static ReplayFrame AppFrame(double t, uint id, byte sa)
        => new(t, id | sa, 8, new byte[8], FrameFlags.None, IsExtended: true);

    private static ReplayFrame[] FramesFrom(double from, double to, byte sa, int count)
    {
        var frames = new ReplayFrame[count];
        for (var i = 0; i < count; i++)
        {
            var t = from + (to - from) * i / Math.Max(count - 1, 1);
            frames[i] = AppFrame(t, 0x18FA0000u, sa);
        }
        return frames;
    }

    private static NmProfile Profile(
        NmMessageDef def, NmTimings? timings = null, NmBitRole[]? bitRoles = null)
        => new(NmProfileSource.Dbc, new[] { def }, timings ?? new NmTimings(),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance(),
            StandardBitRoles: bitRoles);

    private static NmSilencePhaseResult AnalyzeDefault(
        double gapStart, double gapEnd, IReadOnlyList<ReplayFrame> appFrames,
        bool tail = false, NmGlobalTiming? timing = null)
        => AnalyzeWithDef(NmDef, gapStart, gapEnd, appFrames, tail, timing);

    private static NmSilencePhaseResult AnalyzeWithDef(
        NmMessageDef def, double gapStart, double gapEnd,
        IReadOnlyList<ReplayFrame> appFrames, bool tail = false,
        NmGlobalTiming? timing = null, NmBitRole[]? bitRoles = null,
        byte[]? lastPayload = null)
    {
        var payload = lastPayload ?? new byte[8];
        var id = uint.Parse(def.CanIdHex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var nmFrame = new NmFrame(def,
            new ReplayFrame(gapStart - 0.5, id, 8, payload, FrameFlags.None, def.IsExtended));
        var episode = new NmEpisode(0, gapStart - 2, gapStart, "active", null,
            new[] { def.CanIdHex }, BoundaryStart: false, BoundaryEnd: false);
        var all = appFrames.Append(nmFrame.Frame).OrderBy(f => f.Timestamp).ToArray();
        return NmSilencePhaseAnalyzer.Analyze(new NmSilencePhaseInput(
            episode, gapStart, gapEnd, tail, new[] { nmFrame }, all,
            Profile(def, new NmTimings(CycleMs: 500, TimeoutMs: 2000), bitRoles),
            timing ?? NmTimingDefaults.FromProfile(new NmTimings(CycleMs: 500, TimeoutMs: 2000)),
            WindowEnd: gapEnd));
    }

    private static NmSilencePhaseResult AnalyzeWithNmSts(byte lastFrameByte3)
    {
        var payload = new byte[8];
        payload[3] = lastFrameByte3;
        var roles = new[] { new NmBitRole(Byte: 3, Bit: 0, Name: "NmSts") };
        // 12.6–12.69 在下个 episode 起点 13 的唤醒宽限内，提供 w2 的活动证据。
        var app = FramesFrom(10.1, 11.8, sa: 0xF4, count: 10)
            .Concat(FramesFrom(12.6, 12.69, sa: 0xF4, count: 3)).ToArray();
        return AnalyzeWithDef(NmDef, 10, 13, app, lastPayload: payload, bitRoles: roles);
    }

    [Fact]
    public void Gap_WithNmSenderAppFramesInReleaseWindow_EmitsReleaseWaitStillActive()
    {
        var r = AnalyzeDefault(10, 13, FramesFrom(10.1, 11.9, sa: 0xF4, count: 20));

        var phase = r.Phases.Single(p => p.Kind == NmSilencePhaseKind.ReleaseWait);
        phase.Basis.Should().Be(NmSilenceBasis.NmSenderStillActive);
        phase.NmSenderAppFrameCount.Should().Be(20);
        phase.EvidenceKind.Should().Be("inferred");
    }

    [Fact]
    public void Gap_WithAppFramesStoppingAtTimeout_EmitsPrepareSleep()
    {
        var app = FramesFrom(10.1, 11.8, sa: 0xF4, count: 20)
            .Concat(FramesFrom(12.6, 12.69, sa: 0xF4, count: 3)).ToArray();

        var r = AnalyzeDefault(10, 13, app);

        var prepare = r.Phases.Single(p => p.Kind == NmSilencePhaseKind.PrepareSleep);
        prepare.Start.Should().BeApproximately(12.0, 1e-9);
        prepare.End.Should().BeApproximately(13.0, 1e-9);
        prepare.NmSenderAppFrameCount.Should().Be(3, "宽限内的清缓存/唤醒帧仍计数但不阻断");
    }

    [Fact]
    public void Gap_WithNmSenderAppFramesBeyondDrainGrace_EmitsUnknownContradiction()
    {
        // gap 10–15：w2 = [12,14)，宽限外中间段 (12.5,13.5) 仍有帧 → 矛盾。
        var r = AnalyzeDefault(10, 15, FramesFrom(10.1, 13.4, sa: 0xF4, count: 40));

        r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.Unknown
            && p.Basis == NmSilenceBasis.AppTxAfterTimeout);
        r.Unknowns.Should().Contain(NmSilencePhaseUnknowns.AppTxAfterTimeout);
    }

    [Fact]
    public void Gap_ShortPrepareWindowWithContinuousTraffic_EmitsUnknown()
    {
        // review HIGH-1：w2 短于两个宽限之和、判据窗被夹空时，
        // 连续 100ms 应用流量不得判 PrepareSleep。
        var app = FramesFrom(10.1, 12.85, sa: 0xF4, count: 29);

        var r = AnalyzeDefault(10, 12.9, app);

        r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.Unknown
            && p.Basis == NmSilenceBasis.AppTxAfterTimeout);
        r.Phases.Should().NotContain(p => p.Kind == NmSilencePhaseKind.PrepareSleep);
    }

    [Fact]
    public void TailGap_ShorterThanDrainGraceWithFrames_EmitsUnknownWindowTooShort()
    {
        // review HIGH-2：w2 只有 34ms，观察不到发送者是否在清缓存后停发。
        var r = AnalyzeDefault(10, 12.034,
            FramesFrom(12.01, 12.03, sa: 0xF4, count: 3), tail: true);

        r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.Unknown
            && p.Basis == NmSilenceBasis.WindowTooShort);
        r.Phases.Should().NotContain(p => p.Kind == NmSilencePhaseKind.PrepareSleep);
        r.Unknowns.Should().Contain(NmSilencePhaseUnknowns.WindowTooShort);
    }

    [Fact]
    public void Gap_WithOnlyUnattributedFramesInReleaseWindow_EmitsUnknown()
    {
        // review MEDIUM-4：未归属帧可能包含本网络发送者的应用报文，不得作为释放依据。
        var roles = Array.Empty<NmBitRole>();
        var app = Enumerable.Range(0, 10)
            .Select(i => new ReplayFrame(10.1 + i * 0.15, 0x123u, 8, new byte[8],
                FrameFlags.None, IsExtended: false))
            .ToArray();

        var r = AnalyzeWithDef(NmDef, 10, 13, app, bitRoles: roles);

        r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.Unknown
            && p.Basis == NmSilenceBasis.Unattributed);
        r.Phases.Should().NotContain(p => p.Kind == NmSilencePhaseKind.ReleaseWait);
    }

    [Fact]
    public void Gap_ShortPrepareWindowWith500msTraffic_EmitsUnknown()
    {
        // review NEW-M1：帧间隔恰好等于 DrainGrace（500ms）也是持续周期流量，
        // 不得因为 0.5 < 0.5 为假而判 PrepareSleep。
        var app = new List<ReplayFrame>();
        for (var t = 10.1; t <= 12.6 + 1e-9; t += 0.5)
            app.Add(AppFrame(t, 0x18FA0000u, 0xF4));

        var r = AnalyzeDefault(10, 12.9, app);

        r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.Unknown
            && p.Basis == NmSilenceBasis.AppTxAfterTimeout);
        r.Phases.Should().NotContain(p => p.Kind == NmSilencePhaseKind.PrepareSleep);
    }

    [Fact]
    public void Gap_NonEmptyQuietWindow_WithMaxQuietStretchAtDrainGrace_EmitsUnknown()
    {
        // review NEW-M1 第二处：非空判据窗分支也必须检查最长安静跨度。
        // 帧在 12.45（清缓存宽限内）与 12.70（唤醒宽限边界），判据窗 [12.5,12.7) 为空，
        // maxQuietStretch 恰好 0.5s → 不得判 PrepareSleep。
        var app = new[] { AppFrame(12.45, 0x18FA0000u, 0xF4), AppFrame(12.70, 0x18FA0000u, 0xF4) };

        var r = AnalyzeDefault(10, 13.2, app);

        r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.Unknown
            && p.Basis == NmSilenceBasis.AppTxAfterTimeout
            && Math.Abs(p.Start - 12.0) < 1e-9);
        r.Phases.Should().NotContain(p => p.Kind == NmSilencePhaseKind.PrepareSleep);
    }

    [Fact]
    public void InterEpisodeGap_BoundaryKnown_NotTruncated()
    {
        var app = FramesFrom(10.1, 11.8, sa: 0xF4, count: 10)
            .Concat(FramesFrom(12.6, 12.69, sa: 0xF4, count: 3)).ToArray();

        var r = AnalyzeDefault(10, 13, app);

        r.Phases.Single(p => p.Kind == NmSilencePhaseKind.PrepareSleep)
            .IsWindowTruncated.Should().BeFalse("间隙终点由下一 episode 界定，不是窗口截断");
    }

    [Fact]
    public void SleepWindow_WithNmSenderAppFrames_EmitsUnknownContradiction()
    {
        var timing = NmTimingDefaults.FromProfile(new NmTimings(WaitBusSleepMs: 2000));
        var app = FramesFrom(10.1, 11.8, sa: 0xF4, count: 10)
            .Append(AppFrame(15.0, 0x18FA0000u, 0xF4))
            .Append(AppFrame(17.0, 0x18FA0000u, 0xF4))
            .ToArray();

        var r = AnalyzeDefault(10, 17, app, tail: false, timing: timing);

        r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.Unknown
            && p.Basis == NmSilenceBasis.AppTxAfterTimeout
            && Math.Abs(p.Start - 14.0) < 1e-9);
        r.Phases.Should().NotContain(p => p.Kind == NmSilencePhaseKind.Sleep);
    }

    [Fact]
    public void Gap_WithNoFrames_EmitsSingleUnknownNoPhases()
    {
        var r = AnalyzeDefault(10, 13, Array.Empty<ReplayFrame>());

        r.Phases.Should().ContainSingle();
        r.Phases[0].Kind.Should().Be(NmSilencePhaseKind.Unknown);
        r.Phases[0].Basis.Should().Be(NmSilenceBasis.NoBusActivity);
        r.Unknowns.Should().Contain(NmSilencePhaseUnknowns.NoBusActivity);
    }

    [Fact]
    public void Gap_WithOnlyQueueDrainTail_EmitsPrepareSleep()
    {
        // 超时后 0.2s 内的尾巴属于清缓存宽限，不阻断 PrepareSleep。
        var r = AnalyzeDefault(10, 13, FramesFrom(12.05, 12.2, sa: 0xF4, count: 4));

        r.Phases.Should().ContainSingle(p => p.Kind == NmSilencePhaseKind.PrepareSleep);
    }

    [Fact]
    public void Gap_WithUnattributedFrames_EmitsUnknownAndUnknownNote()
    {
        // 标准帧 NM、无 DBC → nmSenders 无法归属。
        var def = NmDef with { CanIdHex = "0x500", IsExtended = false, Sender = null };
        var nmFrame = new NmFrame(def,
            new ReplayFrame(9.5, 0x500u, 8, new byte[8], FrameFlags.None, IsExtended: false));
        var app = new[]
        {
            new ReplayFrame(10.5, 0x123u, 8, new byte[8], FrameFlags.None, IsExtended: false),
            new ReplayFrame(12.5, 0x124u, 8, new byte[8], FrameFlags.None, IsExtended: false),
        };
        var episode = new NmEpisode(0, 8, 10, "active", null,
            SingleParticipant, BoundaryStart: false, BoundaryEnd: false);
        var r = NmSilencePhaseAnalyzer.Analyze(new NmSilencePhaseInput(
            episode, 10, 13, false, new[] { nmFrame }, app.Append(nmFrame.Frame).ToArray(),
            Profile(def), NmTimingDefaults.FromProfile(new NmTimings()), 13));

        r.Phases.Should().OnlyContain(p => p.Kind == NmSilencePhaseKind.Unknown);
        r.Unknowns.Should().Contain(NmSilencePhaseUnknowns.Unattributed);
    }

    [Fact]
    public void Gap_WithNmStsRepeatAtLastFrame_EmitsUnknownNoPhases()
    {
        var r = AnalyzeWithNmSts(lastFrameByte3: 0x00);

        r.Phases.Should().ContainSingle(p => p.Kind == NmSilencePhaseKind.Unknown
            && p.Basis == NmSilenceBasis.NmStsRepeatMessage);
        r.Phases.Should().NotContain(p => p.Kind == NmSilencePhaseKind.PrepareSleep);
        r.Unknowns.Should().Contain(NmSilencePhaseUnknowns.NmStsRepeat);
    }

    [Fact]
    public void Gap_WithNmStsReadySleep_StrengthensBasisButStillInferred()
    {
        var r = AnalyzeWithNmSts(lastFrameByte3: 0x01);

        var p = r.Phases.Single(x => x.Kind == NmSilencePhaseKind.PrepareSleep);
        p.NmSts.Should().Be(NmStsEvidence.ReadySleepAtLastFrame);
        p.Detail.Should().Contain("NMSts=Ready Sleep");
        p.Detail.Should().NotContain("实测", "位值可实测，但整条相位是推断，措辞不得让 AI 误读为状态实测");
        p.EvidenceKind.Should().Be("inferred");
    }

    [Fact]
    public void Gap_WithoutNmStsRole_DoesNotMentionNmSts()
    {
        var r = AnalyzeDefault(10, 13, FramesFrom(10.1, 11.8, sa: 0xF4, count: 10));

        r.Phases.Should().OnlyContain(p => !p.Detail.Contains("NMSts", StringComparison.Ordinal));
    }

    [Fact]
    public void Gap_WithWakeupAppFramesWithinGrace_DoesNotContradict()
    {
        var app = FramesFrom(10.1, 11.8, sa: 0xF4, count: 10)
            .Concat(FramesFrom(12.6, 12.69, sa: 0xF4, count: 3)).ToArray();

        var r = AnalyzeDefault(10, 13, app);

        r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.PrepareSleep);
        r.Phases.Should().NotContain(p => p.Basis == NmSilenceBasis.AppTxAfterTimeout);
    }

    [Fact]
    public void TailGap_LastPhase_IsWindowTruncated()
    {
        var r = AnalyzeDefault(10, 14, FramesFrom(10.1, 12.5, sa: 0xF4, count: 10), tail: true);

        r.Phases.Should().Contain(p => p.IsWindowTruncated && Math.Abs(p.End - 14) < 1e-9);
    }

    [Fact]
    public void SleepWindow_WithoutExplicitWaitBusSleep_EmitsUnknownDefaulted()
    {
        var r = AnalyzeDefault(10, 17, FramesFrom(10.1, 11.8, sa: 0xF4, count: 10));

        r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.Unknown
            && p.Basis == NmSilenceBasis.WaitBusSleepDefaulted);
        r.Phases.Should().NotContain(p => p.Kind == NmSilencePhaseKind.Sleep);
        r.Unknowns.Should().Contain(NmSilencePhaseUnknowns.WaitBusSleepDefaulted);
    }

    [Fact]
    public void SleepWindow_WithExplicitWaitBusSleepAndLiveBus_EmitsSleep()
    {
        var timing = NmTimingDefaults.FromProfile(new NmTimings(WaitBusSleepMs: 2000));
        var app = FramesFrom(10.1, 11.8, sa: 0xF4, count: 10)
            .Append(AppFrame(17.0, 0x18FA0000u, 0xF4)).ToArray();

        var r = AnalyzeDefault(10, 17, app, tail: false, timing: timing);

        r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.Sleep);
    }

    [Fact]
    public void PhaseBoundaries_UseGlobalTimingNotPerMessageOverride()
    {
        var def = NmDef with { TimeoutMs = 5000 };
        var app = FramesFrom(10.1, 11.8, sa: 0xF4, count: 10)
            .Concat(FramesFrom(12.6, 12.69, sa: 0xF4, count: 3)).ToArray();

        var r = AnalyzeWithDef(def, 10, 13, app);

        r.Phases.Single(p => p.Kind == NmSilencePhaseKind.PrepareSleep)
            .Start.Should().BeApproximately(12.0, 1e-9);
    }

    [Fact]
    public void Phases_NeverProduceFindings()
    {
        typeof(NmSilencePhaseResult).GetProperty("Findings").Should().BeNull();
    }
}
