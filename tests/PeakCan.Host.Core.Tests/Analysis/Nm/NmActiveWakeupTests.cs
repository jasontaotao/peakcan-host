using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.3：主动唤醒判定的降级版——只报「疑似不一致」，不声称验证了唤醒源。
/// <para>
/// <b>为什么只能报「疑似」</b>：唤醒源不可观测（§3.1 第 3 条）。trace 里能看到的
/// 只有「快发形态」与「AWB 位取值」，两者的组合既可能是本地唤醒、也可能是网关
/// 跨网段转发的被动唤醒。结论必须标 <c>inferred</c>，文案不得声称验证了主动/被动。
/// </para>
/// <para>
/// <b>测试数据的窗口起点约定</b>：所有帧从 <c>t=2.0</c> 起、窗口从 <c>t=0</c> 起。
/// 这样 <c>episode.BoundaryStart</c> 为 false，不会被「节点在窗口前已活动」的
/// indeterminate 抑制掉——那条抑制有专门的负例（<see cref="NodeActiveBeforeWindow_IsIndeterminate"/>）。
/// </para>
/// </summary>
public class NmActiveWakeupTests
{
    private static readonly string[] CannmReasons = ["MsgType=CANNM"];

    /// <summary>所有测试帧的起点。晚于窗口起点，避免 BoundaryStart 抑制。</summary>
    private const double T0 = 2.0;

    private static NmProfile Profile() => new(
        NmProfileSource.Dbc,
        [
            new NmMessageDef("0x18FFFAF4", true, "NM_A", "A", 8, true,
                CannmReasons, CycleMs: 500, TimeoutMs: 2000),
        ],
        new NmTimings(CycleMs: 500, TimeoutMs: 2000,
            ImmediateCycleMs: 20, ImmediateCount: 10),
        [new NmNodeDef("A", StationAddress: 0xF4, IsNmNode: true)],
        Array.Empty<NmConfigConflict>(),
        Array.Empty<string>(),
        new NmProvenance(),
        Layout: new NmLayout(NidByte: 0, CbvByte: 1),
        Features: new NmFeatures(PnEnabled: false, ActiveWakeupBitEnabled: true));

    private static NmProfile ProfileWithAwbRole() => Profile() with
    {
        StandardBitRoles = [new NmBitRole(1, 4, "ActiveWakeup")],
    };

    // byte1 = CBV（bit4 = AWB），byte3 = NMSts。
    private static ReplayFrame Frame(double offset, bool awb, byte nmsts = 0, byte dlc = 8)
        => new(T0 + offset, 0x18FFFAF4, dlc,
            new byte[] { 0xF4, (byte)(awb ? 0x10 : 0x00), 0x00, nmsts, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None, IsExtended: true);

    private static NmReport Run(
        IReadOnlyList<ReplayFrame> frames, NmProfile? profile = null, double end = 10.0)
        => NmAnalyzer.Analyze(new NmAnalysisRequest(
            "awb", null, frames, null, profile ?? ProfileWithAwbRole(), 0, end));

    private static NmFinding? Suspect(NmReport report)
        => report.Findings.FirstOrDefault(f => f.Code == NmActiveWakeupAnalyzer.SuspectCode);

    // —— 两处矛盾 ——

    [Fact]
    public void BurstWithAwbZero_ReportsSuspect()
    {
        // 20ms 快发 5 帧（4 个短间隔），AWB 全 0 → 有快发却不声明主动唤醒。
        var frames = new[]
        {
            Frame(0.0, false), Frame(0.02, false), Frame(0.04, false),
            Frame(0.06, false), Frame(0.08, false),
            Frame(0.6, false, nmsts: 1),
        };

        var finding = Suspect(Run(frames));

        Assert.NotNull(finding);
        Assert.True(finding!.IsInference, "唤醒源不可观测，结论必须标推断");
        Assert.Equal("info", finding.Severity);
        Assert.Contains("疑似", finding.Message, StringComparison.Ordinal);
        Assert.Contains("未验证主动/被动唤醒", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AwbOneWithoutBurst_ReportsSuspect()
    {
        // 500ms 匀速、AWB=1 → 声明主动唤醒却没有快发。
        var frames = new[]
        {
            Frame(0.0, true), Frame(0.5, true), Frame(1.0, true),
            Frame(1.5, true, nmsts: 1),
        };

        var finding = Suspect(Run(frames));

        Assert.NotNull(finding);
        Assert.True(finding!.IsInference);
        Assert.Equal("info", finding.Severity);
    }

    // —— 一致，不报 ——

    [Fact]
    public void BurstWithAwbOne_NoSuspect()
    {
        var frames = new[]
        {
            Frame(0.0, true), Frame(0.02, true), Frame(0.04, true),
            Frame(0.06, true), Frame(0.08, true),
            Frame(0.6, true, nmsts: 1),
        };

        Assert.Null(Suspect(Run(frames)));
    }

    [Fact]
    public void NoBurstWithAwbZero_NoSuspect()
    {
        var frames = new[]
        {
            Frame(0.0, false), Frame(0.5, false),
            Frame(1.0, false), Frame(1.5, false, nmsts: 1),
        };

        Assert.Null(Suspect(Run(frames)));
    }

    // —— 锚点语义：中途快发不算候选 ——

    /// <summary>
    /// <b>锚点约束</b>：快发段必须在 episode 开头。报文先正常发送 500ms、中途才出现
    /// 快发时，那不是「唤醒段」，AWB=0 也不矛盾。
    /// <para>
    /// 首版实现用「任意位置连续 2 个短间隔」，会把这个场景误报成疑似——这正是
    /// <see cref="NmBurstDetector"/> 注释里说的「先正常发送、后唤醒」误判。
    /// </para>
    /// </summary>
    [Fact]
    public void BurstInMiddleNotAtAnchor_NoSuspect()
    {
        var frames = new[]
        {
            Frame(0.0, false), Frame(0.5, false), Frame(1.0, false),
            // 中途快发
            Frame(1.02, false), Frame(1.04, false), Frame(1.06, false),
            Frame(1.6, false, nmsts: 1),
        };

        Assert.Null(Suspect(Run(frames)));
    }

    /// <summary>
    /// 反向：AWB=1 且只有中途快发 → 锚点处没有快发 → 应报疑似。
    /// 首版实现会因「整段有快发」而吞掉这条。
    /// </summary>
    [Fact]
    public void AwbOneWithOnlyMidBurst_ReportsSuspect()
    {
        var frames = new[]
        {
            Frame(0.0, true), Frame(0.5, true), Frame(1.0, true),
            Frame(1.02, true), Frame(1.04, true), Frame(1.06, true),
            Frame(1.6, true, nmsts: 1),
        };

        Assert.NotNull(Suspect(Run(frames)));
    }

    // —— 恰好 1 个短间隔不算候选（spec：连续 ≥2） ——

    [Fact]
    public void SingleShortInterval_IsNotCandidate()
    {
        // 0 → 0.02 只有一个短间隔，随后长间隔。AWB=0 → 无矛盾，不报。
        var frames = new[]
        {
            Frame(0.0, false), Frame(0.02, false), Frame(0.5, false),
            Frame(1.0, false, nmsts: 1),
        };

        Assert.Null(Suspect(Run(frames)));
    }

    // —— 重复时间戳不能造出假快发 ——

    /// <summary>
    /// 重复帧（同一时间戳）会产生 gap=0 的「短间隔」。若不按时间戳去重，
    /// 两个重复帧就能凑成「连续 2 个短间隔」→ 假候选 → 假疑似。
    /// 真实 trace 里重复帧是已知情况（<c>duplicate_frame_attribution</c>）。
    /// </summary>
    [Fact]
    public void DuplicateTimestamps_DoNotCreateFalseBurst()
    {
        var frames = new[]
        {
            Frame(0.0, false), Frame(0.0, false), Frame(0.0, false),
            Frame(0.5, false), Frame(1.0, false, nmsts: 1),
        };

        Assert.Null(Suspect(Run(frames)));
    }

    // —— 不判的边界 ——

    /// <summary>位角色缺失：读不出 AWB，报「疑似」等于凭空断言。</summary>
    [Fact]
    public void NoAwbRole_DoesNotReportSuspect()
    {
        var frames = new[]
        {
            Frame(0.0, false), Frame(0.02, false), Frame(0.04, false),
            Frame(0.06, false), Frame(0.6, false, nmsts: 1),
        };

        // 用不带 AWB 角色的 profile：行为是「不报」，且这是唯一的正确行为
        // （分析器不产 Unmodeled，那是规则引擎的职责）。
        Assert.Null(Suspect(Run(frames, Profile())));
    }

    /// <summary>单帧没有「间隔」可言。用 AWB=1 构造，确保守卫真的在起作用。</summary>
    [Fact]
    public void SingleFrame_DoesNotReportSuspect()
    {
        // AWB=1 且无快发：若无守卫，会判成矛盾并报疑似。
        Assert.Null(Suspect(Run([Frame(0.0, true)])));
    }

    /// <summary>
    /// <b>节点在窗口前已活动</b>：首帧就是窗口起点 → 它不是唤醒首帧，
    /// AWB 可能早已被清 0。spec §7.3 明确要求判 indeterminate。
    /// </summary>
    [Fact]
    public void NodeActiveBeforeWindow_IsIndeterminate()
    {
        // 帧从 t=0（= 窗口起点）开始，有快发 + AWB=0——若无抑制会误报。
        var frames = new[]
        {
            Frame(-T0, false), Frame(-T0 + 0.02, false), Frame(-T0 + 0.04, false),
            Frame(-T0 + 0.06, false), Frame(-T0 + 0.6, false, nmsts: 1),
        };

        Assert.Null(Suspect(Run(frames)));
    }

    /// <summary>首帧 DLC 不足读不出 AWB：不臆造 0。</summary>
    [Fact]
    public void AnchorDlcTooShort_DoesNotReportSuspect()
    {
        // 首帧 DLC=1（只到 byte0），AWB 在 byte1 读不出。
        //
        // 构造要点：必须让「无守卫」时**会误报**，否则这条测试抓不到守卫。
        // 用 AWB 位=0 + 有快发：无守卫时 ReadBit 读到 Data[1]=0x00 → 0 vs 有快发
        // → 矛盾 → 报疑似；有守卫时 Dlc=1 <= byte1 → 读不出 → 跳过。
        // （Data.Length 恒为 8，BLF 经典帧固定取 8 字节，所以只查长度拦不住。）
        var frames = new[]
        {
            Frame(0.0, false, dlc: 1), Frame(0.02, false), Frame(0.04, false),
            Frame(0.06, false), Frame(0.6, false, nmsts: 1),
        };

        Assert.Null(Suspect(Run(frames)));
    }

    /// <summary>
    /// 段尾被窗口截断：快发可能延伸到窗口外，形态不可信 → 不判。
    /// 窗口末端紧贴末帧（差值 &lt; timeout）会让 episode.BoundaryEnd 为真。
    /// </summary>
    [Fact]
    public void BurstTruncatedAtWindowEnd_IsIndeterminate()
    {
        var frames = new[]
        {
            Frame(0.0, false), Frame(0.02, false), Frame(0.04, false),
            Frame(0.06, false),
        };

        // 窗口末端 = 末帧 + 0.1s（< 2s timeout）→ BoundaryEnd=true 且末帧触到 episode.End。
        Assert.Null(Suspect(Run(frames, end: T0 + 0.16)));
    }
}
