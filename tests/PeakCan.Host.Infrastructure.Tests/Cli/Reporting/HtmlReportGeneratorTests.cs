using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.Cli.Reporting;

namespace PeakCan.Host.Infrastructure.Tests.Cli.Reporting;

public class HtmlReportGeneratorTests
{
    private static TestSuiteResult CreateResult(int passed, int failed, int skipped = 0)
    {
        var cases = new List<TestCaseResult>();
        for (int i = 0; i < passed; i++)
            cases.Add(new TestCaseResult($"Pass{i}", $"Pass{i}", true, null, 10, 1, 1, 0, 0, 0, Array.Empty<StepResult>()));
        for (int i = 0; i < failed; i++)
            cases.Add(new TestCaseResult($"Fail{i}", $"Fail{i}", false, "assertion failed", 10, 1, 0, 1, 0, 0, new[]
            {
                new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed, "out of tolerance", "5", "10", 0)
            }));
        return new TestSuiteResult("Suite", passed + failed + skipped, passed, failed, skipped, 100, Array.Empty<string>(), cases);
    }

    [Fact]
    public void HtmlReport_AllPassed_GeneratesSummaryWithPassRate()
    {
        var result = CreateResult(passed: 3, failed: 0);
        var html = HtmlReportGenerator.GenerateHtml(result);

        Assert.Contains("100%", html);
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("Suite", html);
    }

    [Fact]
    public void HtmlReport_WithFailure_IncludesFramesHexDump()
    {
        var frame = new CanFrame(
            new CanId(0x123, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x01, 0x02, 0x03 }),
            FrameFlags.None, ChannelId.None, new Timestamp(1000));

        var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
            "out of tolerance", "5", "10", 0, new[] { frame });

        var caseResult = new TestCaseResult("FailCase", "FailCase", false, "assertion failed", 10, 1, 0, 1, 0, 0, new[] { step });
        var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var html = HtmlReportGenerator.GenerateHtml(result);

        Assert.Contains("Frame", html);
        Assert.Contains("01 02 03", html);
    }

    [Fact]
    public void HtmlReport_FramesCappedAt50_DoesNotCrash()
    {
        var frames = new List<CanFrame>();
        for (int i = 0; i < 60; i++)
        {
            frames.Add(new CanFrame(
                new CanId(0x123, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(new byte[] { (byte)i }),
                FrameFlags.None, ChannelId.None, new Timestamp((ulong)i * 1000)));
        }

        var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
            "fail", null, null, 0, frames);

        var caseResult = new TestCaseResult("FailCase", "FailCase", false, "fail", 10, 1, 0, 1, 0, 0, new[] { step });
        var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var html = HtmlReportGenerator.GenerateHtml(result);

        Assert.Contains("<!DOCTYPE html>", html);
    }

    [Fact]
    public void HtmlReport_NegatedStep_ShowsBadgeWithTooltip()
    {
        // 负测试步骤：引擎提升 Status=Passed，WasNegatedTest=true
        var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Passed,
            "expected failure occurred", null, null, 0)
        { WasNegatedTest = true };

        var caseResult = new TestCaseResult("NegCase", "NegCase", true, null, 10, 1, 1, 0, 0, 0, new[] { step });
        var result = new TestSuiteResult("Suite", 1, 1, 0, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var html = HtmlReportGenerator.GenerateHtml(result);

        Assert.Contains("[negated]", html);
        Assert.Contains("负测试", html);
    }

    [Fact]
    public void HtmlReport_NegatedStep_UsesNegatedPassClass()
    {
        var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Passed,
            "expected failure occurred", null, null, 0)
        { WasNegatedTest = true };

        var caseResult = new TestCaseResult("NegCase", "NegCase", true, null, 10, 1, 1, 0, 0, 0, new[] { step });
        var result = new TestSuiteResult("Suite", 1, 1, 0, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var html = HtmlReportGenerator.GenerateHtml(result);

        // 断言步骤行 class 本身（而非 CSS 中同名选择器）
        Assert.Contains("<tr class=\"negated-pass\"", html);
    }

    [Fact]
    public void HtmlReport_NormalPassStep_NoNegatedBadge()
    {
        // WasNegatedTest=false（默认）的通过步骤不渲染 [negated] badge。
        // 注意：不能断言 "negated-pass" 不存在——CSS 规则本身含该字符串；
        // 用 [negated]（仅渲染逻辑产出）作为信号。
        var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Passed,
            "ok", null, null, 0);

        var caseResult = new TestCaseResult("PassCase", "PassCase", true, null, 10, 1, 1, 0, 0, 0, new[] { step });
        var result = new TestSuiteResult("Suite", 1, 1, 0, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var html = HtmlReportGenerator.GenerateHtml(result);

        Assert.DoesNotContain("[negated]", html);
    }

    [Fact]
    public void HtmlReport_WithDbc_DecodesSignalWithEnumText()
    {
        // 内联 DBC：MsgA 信号 SigA (0|2@1+)，VAL_ 表 0-3 映射枚举文本
        const string dbcSrc = """
            VERSION "sanitized-fixture"
            NS_ :
            BS_:
            BO_ 256 MsgA: 8 Vector__XL
             SG_ SigA : 0|2@1+ (1,0) [0|3] "bit" Vector__XL
            VAL_ 256 SigA 0 "Zero" 1 "One" 2 "Two" 3 "Three" ;
            """;
        var doc = DbcParser.Parse(dbcSrc).Value!;

        // data[0] 低 2 位 = 0b10 = 2 → 枚举文本 "Two"
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x02 }),
            FrameFlags.None, ChannelId.None, new Timestamp(1000));

        var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
            "out of tolerance", "2", "3", 0, new[] { frame });
        var caseResult = new TestCaseResult("FailCase", "FailCase", false, "assertion failed", 10, 1, 0, 1, 0, 0, new[] { step });
        var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var html = HtmlReportGenerator.GenerateHtml(result, fallbackDbc: doc);

        Assert.Contains("SigA=Two", html);
    }

    [Fact]
    public void HtmlReport_WithDbc_EncodesSignalText_AgainstXss()
    {
        // VAL_ 枚举文本含 HTML → 输出必须 HtmlEncode（防 DBC 内容注入）
        const string dbcSrc = """
            VERSION "sanitized-fixture"
            NS_ :
            BS_:
            BO_ 256 MsgA: 8 Vector__XL
             SG_ SigA : 0|2@1+ (1,0) [0|3] "bit" Vector__XL
            VAL_ 256 SigA 0 "<script>alert(1)</script>" ;
            """;
        var doc = DbcParser.Parse(dbcSrc).Value!;

        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x00 }),
            FrameFlags.None, ChannelId.None, new Timestamp(1000));

        var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
            "fail", null, null, 0, new[] { frame });
        var caseResult = new TestCaseResult("FailCase", "FailCase", false, "fail", 10, 1, 0, 1, 0, 0, new[] { step });
        var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var html = HtmlReportGenerator.GenerateHtml(result, fallbackDbc: doc);

        Assert.Contains("SigA=&lt;script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }

    [Fact]
    public void HtmlReport_HasInteractiveSearchFilterStructure()
    {
        // 单元 C：HTML 结构层必须含搜索框 input、步骤行 data-status 属性、状态筛选按钮、内嵌 JS。
        var result = CreateResult(passed: 1, failed: 1);
        var html = HtmlReportGenerator.GenerateHtml(result);

        Assert.Contains("<input", html);              // 搜索框
        Assert.Contains("data-status", html);         // 步骤行 status 属性
        Assert.Contains("data-filter", html);         // 状态筛选按钮
        Assert.Contains("<script>", html);            // 内嵌 JS
        Assert.Contains("step-label", html);          // label 列 class（JS 搜索选择器）
        Assert.Contains("step-message", html);        // message 列 class（JS 搜索选择器）
    }

    [Fact]
    public void HtmlReport_WithDbc_FailureFrames_IncludeSvgTimeline()
    {
        // 单元 D：失败步骤的 FramesAroundFailure 渲染 SVG 信号时序图。
        const string dbcSrc = """
            VERSION "sanitized-fixture"
            NS_ :
            BS_:
            BO_ 256 MsgA: 8 Vector__XL
             SG_ SigA : 0|2@1+ (1,0) [0|3] "bit" Vector__XL
            """;
        var doc = DbcParser.Parse(dbcSrc).Value!;

        var frames = new List<CanFrame>
        {
            new(new CanId(0x100, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(new byte[] { 0x01 }), FrameFlags.None, ChannelId.None, new Timestamp(1000)),
            new(new CanId(0x100, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(new byte[] { 0x03 }), FrameFlags.None, ChannelId.None, new Timestamp(2000)),
        };
        var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
            "fail", null, null, 0, frames);
        var caseResult = new TestCaseResult("FailCase", "FailCase", false, "fail", 10, 1, 0, 1, 0, 0, new[] { step });
        var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var html = HtmlReportGenerator.GenerateHtml(result, fallbackDbc: doc);

        Assert.Contains("class=\"signal-timeline\"", html);   // 时序图 SVG
        Assert.Contains("SigA", html);                        // 信号名（图例/标题）
        Assert.Contains("<polyline", html);                   // 坐标点曲线
    }

    [Fact]
    public void HtmlReport_WithoutDbc_NoSignalTimeline()
    {
        // 无 DBC 时回落 hex 显示，不渲染时序图（向后兼容）
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x01 }),
            FrameFlags.None, ChannelId.None, new Timestamp(1000));
        var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
            "fail", null, null, 0, new[] { frame });
        var caseResult = new TestCaseResult("FailCase", "FailCase", false, "fail", 10, 1, 0, 1, 0, 0, new[] { step });
        var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var html = HtmlReportGenerator.GenerateHtml(result, fallbackDbc: null);

        // 注意：CSS 规则本身含 ".signal-timeline"，需断言 SVG 元素而非 class 字符串
        Assert.DoesNotContain("<svg class=\"signal-timeline\"", html);
    }

    // ── 控制流报告：按 Path 分组缩进 + Iteration + 容器行聚合摘要 ────

    [Fact]
    public void HtmlReport_ControlFlow_IfContainer_ChildStepsIndented()
    {
        // 控制流 case：If 容器（StepIndex=1, Path=null, Kind=If, 聚合摘要）
        // + 3 个子步骤（StepIndex=1, Path="1.0"/"1.1"/"1.2", 共享 StepIndex）
        // 子步骤不应塌成一行，应有缩进 class（非控制流平铺无缩进）
        var ifContainer = new StepResult(1, TestCaseStepKind.If, "Check DTC", StepStatus.Passed,
            "if: 2/3 steps passed", null, null, 0)
        { Path = null, Iteration = null };
        var child1 = new StepResult(1, TestCaseStepKind.AssertDtc, "dtc1", StepStatus.Passed,
            "ok", null, null, 10)
        { Path = "1.0", Iteration = null };
        var child2 = new StepResult(1, TestCaseStepKind.AssertDtc, "dtc2", StepStatus.Failed,
            "not present", null, null, 10)
        { Path = "1.1", Iteration = null };
        var child3 = new StepResult(1, TestCaseStepKind.AssertDtc, "dtc3", StepStatus.Passed,
            "ok", null, null, 10)
        { Path = "1.2", Iteration = null };

        var caseResult = new TestCaseResult("CtrlFlow", "CtrlFlow", false, "if branch failed",
            100, 4, 2, 1, 0, 0,
            new[] { ifContainer, child1, child2, child3 });
        var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var html = HtmlReportGenerator.GenerateHtml(result);

        // 控制流行应有 depth class 缩进（非控制流 <tr> 无 step-depth-* class）
        Assert.Contains("step-depth-2", html);
        // 容器行 depth=0（Path=null）
        Assert.Contains("step-depth-0", html);
        // 聚合摘要消息
        Assert.Contains("if: 2/3 steps passed", html);
        // 子步骤共享 StepIndex=1，但不应塌成一行：应有 3 个 Path 不同的行
        Assert.Contains("data-path=\"1.0\"", html);
        Assert.Contains("data-path=\"1.1\"", html);
        Assert.Contains("data-path=\"1.2\"", html);
    }

    [Fact]
    public void HtmlReport_ControlFlow_LoopIteration_ShownInRow()
    {
        // Repeat 容器 + 子步骤带 Iteration
        var repeatContainer = new StepResult(2, TestCaseStepKind.Repeat, "Retry", StepStatus.Failed,
            "repeat: 0/3 steps passed", null, null, 0)
        { Path = null, Iteration = null };
        var loop1 = new StepResult(2, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
            "out of range", "5", "10", 10)
        { Path = "2.0", Iteration = 0 };
        var loop2 = new StepResult(2, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
            "out of range", "5", "10", 10)
        { Path = "2.1", Iteration = 1 };
        var loop3 = new StepResult(2, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
            "out of range", "5", "10", 10)
        { Path = "2.2", Iteration = 2 };

        var caseResult = new TestCaseResult("LoopCase", "LoopCase", false, "all retries failed",
            100, 4, 0, 4, 0, 0,
            new[] { repeatContainer, loop1, loop2, loop3 });
        var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var html = HtmlReportGenerator.GenerateHtml(result);

        // 循环失败行应显示 Iteration
        Assert.Contains("Iteration 0", html);
        Assert.Contains("Iteration 1", html);
        Assert.Contains("Iteration 2", html);
    }

    [Fact]
    public void HtmlReport_NonControlFlow_Flat_NoIndentation()
    {
        // 非控制流（Path=null, 无容器 Kind）: 平铺，无 depth class
        var step1 = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Passed,
            "ok", null, null, 10);
        var step2 = new StepResult(1, TestCaseStepKind.AssertSignal, "s2", StepStatus.Failed,
            "fail", "5", "10", 10);
        var caseResult = new TestCaseResult("FlatCase", "FlatCase", false, "step 1 failed",
            50, 2, 1, 1, 0, 0, new[] { step1, step2 });
        var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var html = HtmlReportGenerator.GenerateHtml(result);

        // 平铺：<tr> 无 step-depth- class，无 data-path 属性
        Assert.DoesNotContain("class=\"step-depth-", html);
        Assert.DoesNotContain("data-path=\"", html);
        // 仍按 StepIndex 区分
        Assert.Contains(">0<", html);  // StepIndex 0
        Assert.Contains(">1<", html);  // StepIndex 1
    }

    [Fact]
    public void HtmlReport_SvgTimeline_FiltersFramesBySignalMessageId()
    {
        // H1 回归（code review 2026-08-10）：多 CAN ID 混合帧集下，SigA 曲线只应包含
        // 其所属消息（MsgA/0x100）的帧点，不得解码 MsgB 帧（0x200）得到伪值并连入曲线。
        const string dbcSrc = """
            VERSION "demo"
            NS_ :
            BS_:
            BO_ 256 MsgA: 8 ECU
             SG_ SigA : 0|8@1+ (1,0) [0|255] ""  ECU
            BO_ 512 MsgB: 8 ECU
             SG_ SigB : 0|8@1+ (1,0) [0|255] ""  ECU
            """;
        var doc = DbcParser.Parse(dbcSrc).Value!;

        // 帧集：0x100(01), 0x200(03), 0x100(02) —— 中间穿插其它消息帧
        var frames = new List<CanFrame>
        {
            new(new CanId(0x100, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(new byte[] { 0x01 }), FrameFlags.None, ChannelId.None, new Timestamp(1000)),
            new(new CanId(0x200, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(new byte[] { 0x03 }), FrameFlags.None, ChannelId.None, new Timestamp(2000)),
            new(new CanId(0x100, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(new byte[] { 0x02 }), FrameFlags.None, ChannelId.None, new Timestamp(3000)),
        };
        var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
            "fail", null, null, 0, frames);
        var caseResult = new TestCaseResult("FailCase", "FailCase", false, "fail", 10, 1, 0, 1, 0, 0, new[] { step });
        var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var html = HtmlReportGenerator.GenerateHtml(result, fallbackDbc: doc);

        // SigA 只含 MsgA 帧点（0µs/2000µs）；修复前 SigA title 会是
        // "1 @ 0µs, 3 @ 1000µs, 2 @ 2000µs"（含 MsgB 帧伪值 3 的连续段），修复后断为两段。
        // 注：HtmlEncode 将 µ 转义为 &#181;，断言用前缀匹配（单位无关）。
        Assert.Contains("SigA: 1 @ 0", html);
        Assert.DoesNotContain(", 3 @ 1000", html);
        // SigB 只含 MsgB 帧点（1000µs），且不会被 SigA 所在帧污染
        Assert.Contains("SigB: 3 @ 1000", html);
    }
}
