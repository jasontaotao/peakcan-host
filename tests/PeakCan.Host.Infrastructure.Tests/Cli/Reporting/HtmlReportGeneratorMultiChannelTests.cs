using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.Cli.Reporting;

namespace PeakCan.Host.Infrastructure.Tests.Cli.Reporting;

/// <summary>
/// HtmlReportGenerator 多通道路由测试（spec §3.6，Task 11）：
/// dbcs 字典按 frame.Channel 选对应 DbcDocument 解码；fallbackDbc 兜底；
/// StepResult.Channel 非空时步骤行含 "通道:" 标签；旧 3 参数重载向后兼容。
/// </summary>
public class HtmlReportGeneratorMultiChannelTests
{
    // 通道 A：消息 0x100 / 信号 SigA (0|2@1+)，VAL_ 0→"ZeroA" 1→"OneA"
    private const string DbcASrc = """
        VERSION "sanitized-fixture"
        NS_ :
        BS_:
        BO_ 256 MsgA: 8 Vector__XL
         SG_ SigA : 0|2@1+ (1,0) [0|3] "bit" Vector__XL
        VAL_ 256 SigA 0 "ZeroA" 1 "OneA" ;
        """;

    // 通道 B：同一 CAN ID 0x100 但不同信号 SigB + 枚举（验证按通道选 DBC，不是按 ID 混）
    // VAL_ 引用 SigB（非 SigA），DBC 解析器要求 VAL_ 表引用存在的信号。
    private const string DbcBSrc = """
        VERSION "sanitized-fixture"
        NS_ :
        BS_:
        BO_ 256 MsgA: 8 Vector__XL
         SG_ SigB : 0|2@1+ (1,0) [0|3] "bit" Vector__XL
        VAL_ 256 SigB 0 "ZeroB" 1 "OneB" ;
        """;

    private static readonly ChannelId ChA = new(0x51);
    private static readonly ChannelId ChB = new(0x52);

    private static TestSuiteResult ResultWithFailedFrame(CanFrame frame, string? channel = null)
    {
        var step = new StepResult(
            StepIndex: 0,
            Kind: TestCaseStepKind.AssertSignal,
            Label: "s1",
            Status: StepStatus.Failed,
            Message: "out of tolerance",
            ActualValue: "2",
            ExpectedValue: "3",
            ElapsedMs: 0,
            FramesAroundFailure: new[] { frame },
            Channel: channel);
        var caseResult = new TestCaseResult("FailCase", "FailCase", false, "assertion failed",
            10, 1, 0, 1, 0, 0, new[] { step });
        return new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });
    }

    [Fact]
    public void RenderCase_WithDbcsByChannel_DecodesUsingChannelSpecificDbc()
    {
        // 两通道各一份 DBC；帧落在通道 A → 用 DbcA 解码出 SigA=ZeroA
        var dbcs = new Dictionary<ChannelId, DbcDocument>
        {
            [ChA] = DbcParser.Parse(DbcASrc).Value!,
            [ChB] = DbcParser.Parse(DbcBSrc).Value!,
        };
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x00 }),  // 低 2 位 = 0
            FrameFlags.None, ChA, new Timestamp(1000));

        var html = HtmlReportGenerator.GenerateHtml(ResultWithFailedFrame(frame), dbcs: dbcs);

        Assert.Contains("SigA=ZeroA", html);
        Assert.DoesNotContain("SigB", html);  // 不该用 DbcB 的信号名
    }

    [Fact]
    public void RenderCase_WithDbcsByChannel_DifferentChannelSelectsDifferentDbc()
    {
        var dbcs = new Dictionary<ChannelId, DbcDocument>
        {
            [ChA] = DbcParser.Parse(DbcASrc).Value!,
            [ChB] = DbcParser.Parse(DbcBSrc).Value!,
        };
        // 同一 data=0x00，帧在通道 B → 用 DbcB 解码出 SigB=ZeroB（VAL_ 0→"ZeroB"）
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x00 }),
            FrameFlags.None, ChB, new Timestamp(1000));

        var html = HtmlReportGenerator.GenerateHtml(ResultWithFailedFrame(frame), dbcs: dbcs);

        Assert.Contains("SigB=ZeroB", html);
        Assert.DoesNotContain("SigA=", html);
    }

    [Fact]
    public void RenderCase_FramesChannelNotInDbcs_FallsBackToFallbackDbc()
    {
        // 帧通道未在字典中 → 用 fallbackDbc 兜底
        var dbcs = new Dictionary<ChannelId, DbcDocument>
        {
            [ChA] = DbcParser.Parse(DbcASrc).Value!,
        };
        var fallback = DbcParser.Parse(DbcBSrc).Value!;
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x00 }),
            FrameFlags.None, ChB, new Timestamp(1000));  // ChB 不在 dbcs

        var html = HtmlReportGenerator.GenerateHtml(ResultWithFailedFrame(frame), dbcs: dbcs, fallbackDbc: fallback);

        Assert.Contains("SigB=ZeroB", html);  // fallback DbcB 的信号
    }

    [Fact]
    public void RenderCase_StepResultChannelNonEmpty_StepRowContainsChannelLabel()
    {
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x01 }),
            FrameFlags.None, ChA, new Timestamp(1000));
        // StepResult.Channel = "bus-a"（非空）
        var result = ResultWithFailedFrame(frame, channel: "bus-a");

        var html = HtmlReportGenerator.GenerateHtml(result);

        Assert.Contains("通道: bus-a", html);
    }

    [Fact]
    public void RenderCase_StepResultChannelNull_NoChannelLabel()
    {
        // Channel=null（单通道默认）→ 不显通道标签（不污染单通道报告）
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x01 }),
            FrameFlags.None, ChannelId.None, new Timestamp(1000));
        var result = ResultWithFailedFrame(frame, channel: null);

        var html = HtmlReportGenerator.GenerateHtml(result);

        Assert.DoesNotContain("通道:", html);
    }

    [Fact]
    public void GenerateHtml_LegacyDbcOverload_ForwardsToFallbackDbc()
    {
        // 旧 3 参数重载 (result, trends, dbc) → 等价于 dbcs:null, fallbackDbc:dbc
        var dbc = DbcParser.Parse(DbcASrc).Value!;
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x00 }),
            FrameFlags.None, ChA, new Timestamp(1000));
        var result = ResultWithFailedFrame(frame);

        var html = HtmlReportGenerator.GenerateHtml(result, fallbackDbc: dbc);

        Assert.Contains("SigA=ZeroA", html);  // 旧重载仍按 dbc 解码
    }
}
