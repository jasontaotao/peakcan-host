using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Analysis;
using PeakCan.HIL.Core.HIL.Expressions;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.Multichannel;

/// <summary>
/// §2.4 UDS 通道配置校验（Task 10，spec 2026-08-27）：
/// MC-3  UDS 步骤 TargetChannel 指向的通道无 UDS ID 配置 → High
/// MC-4  同通道 UdsRequestId == UdsResponseId → High
/// MC-5  与其他通道 UDS ID 冲突 → Medium
/// 纯帧步骤（SendFrame 等）路由到无 UDS 配置通道合法（不触发 MC-3）。
/// </summary>
public sealed class UdsChannelConfigValidatorTests
{
    private static readonly ExpressionEvaluator Evaluator = new();

    private static StepValidatorRegistry NewRegistry() => new(Evaluator, dbcLookup: null);

    private static TestCase CaseWithUdsStep(string caseId, string? targetChannel)
    {
        var p = new ReadDidStep(0xF190) { TargetChannel = targetChannel };
        var step = TestCaseStep.Create(p, label: "read-did");
        return new TestCase(caseId, caseId, "desc", null,
            new[] { step }, null, Array.Empty<string>());
    }

    private static TestCase CaseWithFrameStep(string caseId, string? targetChannel)
    {
        var p = new SendFrameStep(new CanId(0x123, FrameFormat.Standard), new byte[] { 0x01 }, false, false)
        {
            TargetChannel = targetChannel,
        };
        var step = TestCaseStep.Create(p, label: "send");
        return new TestCase(caseId, caseId, "desc", null,
            new[] { step }, null, Array.Empty<string>());
    }

    private static TestSuite SuiteWith(params ChannelConfig[] channels) => new(
        "s", new[] { CaseWithUdsStep("c1", "bus-a") },
        Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), Channels: channels);

    private static ChannelConfig Bus(string name, uint? req, uint? resp)
        => new(name, $"h-{name}", BaudRate.Can500kbps, false, null, req, resp);

    // ---- MC-3: UDS 步骤路由到无 UDS ID 配置的通道 ----

    [Fact]
    public void UdsStep_TargetChannel_ChannelWithoutUdsIds_High()
    {
        var suite = SuiteWith(Bus("bus-a", null, null));   // 声明通道但无 UDS ID

        var issues = NewRegistry().Validate(suite);

        var mc3 = issues.FirstOrDefault(i => i.RuleId == "MC-3");
        Assert.NotNull(mc3);
        Assert.Equal(ValidationSeverity.High, mc3!.Severity);
        Assert.Contains("bus-a", mc3.Message);
        Assert.Contains("UDS", mc3.Message);
    }

    [Fact]
    public void UdsStep_TargetChannel_ChannelWithUdsIds_NoIssue()
    {
        var suite = SuiteWith(Bus("bus-a", 0x7E0, 0x7E8));   // 有 UDS ID → 合法

        var issues = NewRegistry().Validate(suite);

        Assert.DoesNotContain(issues, i => i.RuleId == "MC-3");
    }

    [Fact]
    public void UdsStep_TargetChannel_ChannelWithPartialUdsIds_High()
    {
        // 只配一个 ID → 运行时无 per-channel 栈（HeadlessHostBuilder 要求两者非空）→
        // resolver fallback 默认栈 → 静默读错 ECU。必须 High（review MEDIUM 修复）。
        var suite = SuiteWith(Bus("bus-a", 0x7E0, null));   // 缺 UdsResponseId

        var issues = NewRegistry().Validate(suite);

        var mc3 = issues.FirstOrDefault(i => i.RuleId == "MC-3");
        Assert.NotNull(mc3);
        Assert.Equal(ValidationSeverity.High, mc3!.Severity);
        Assert.Contains("missing UdsResponseId", mc3.Message);
    }

    [Fact]
    public void FrameStep_TargetChannel_ChannelWithoutUdsIds_NoIssue()
    {
        // 纯帧步骤不需要 UDS 配置：SendFrame 路由到无 UDS ID 通道合法
        var channels = new[] { Bus("bus-a", null, null) };
        var suite = new TestSuite("s", new[] { CaseWithFrameStep("c1", "bus-a") },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), Channels: channels);

        var issues = NewRegistry().Validate(suite);

        Assert.DoesNotContain(issues, i => i.RuleId == "MC-3");
    }

    [Fact]
    public void UdsStep_NoTargetChannel_NoIssue()
    {
        // 无 TargetChannel = 默认栈，无法在 suite 级校验（由 CLI 层 UdsRequestId/UdsResponseId 配置）
        var suite = new TestSuite("s", new[] { CaseWithUdsStep("c1", targetChannel: null) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig());

        var issues = NewRegistry().Validate(suite);

        Assert.DoesNotContain(issues, i => i.RuleId == "MC-3");
    }

    // ---- MC-4: 同通道 RequestId == ResponseId ----

    [Fact]
    public void SameChannel_RequestEqualsResponse_High()
    {
        var suite = SuiteWith(Bus("bus-a", 0x7E0, 0x7E0));   // request == response

        var issues = NewRegistry().Validate(suite);

        var mc4 = issues.FirstOrDefault(i => i.RuleId == "MC-4");
        Assert.NotNull(mc4);
        Assert.Equal(ValidationSeverity.High, mc4!.Severity);
        Assert.Contains("bus-a", mc4.Message);
    }

    [Fact]
    public void SameChannel_RequestDiffersResponse_NoIssue()
    {
        var suite = SuiteWith(Bus("bus-a", 0x7E0, 0x7E8));

        var issues = NewRegistry().Validate(suite);

        Assert.DoesNotContain(issues, i => i.RuleId == "MC-4");
    }

    // ---- MC-5: 跨通道 UDS ID 冲突 ----

    [Fact]
    public void CrossChannel_RequestConflicts_Medium()
    {
        // bus-b request 0x7E0 与 bus-a request 0x7E0 冲突
        var suite = SuiteWith(Bus("bus-a", 0x7E0, 0x7E8), Bus("bus-b", 0x7E0, 0x6E8));

        var issues = NewRegistry().Validate(suite);

        var mc5 = issues.FirstOrDefault(i => i.RuleId == "MC-5");
        Assert.NotNull(mc5);
        Assert.Equal(ValidationSeverity.Medium, mc5!.Severity);
        Assert.Contains("0x7E0", mc5.Message);
    }

    [Fact]
    public void CrossChannel_RequestVsResponseConflicts_Medium()
    {
        // bus-b response 0x7E0 与 bus-a request 0x7E0 冲突（request/response 跨通道重叠）
        var suite = SuiteWith(Bus("bus-a", 0x7E0, 0x7E8), Bus("bus-b", 0x6E0, 0x7E0));

        var issues = NewRegistry().Validate(suite);

        var mc5 = issues.FirstOrDefault(i => i.RuleId == "MC-5");
        Assert.NotNull(mc5);
        Assert.Equal(ValidationSeverity.Medium, mc5!.Severity);
    }

    [Fact]
    public void CrossChannel_ResponseConflicts_Medium()
    {
        var suite = SuiteWith(Bus("bus-a", 0x7E0, 0x7E8), Bus("bus-b", 0x6E0, 0x7E8));

        var issues = NewRegistry().Validate(suite);

        var mc5 = issues.FirstOrDefault(i => i.RuleId == "MC-5");
        Assert.NotNull(mc5);
        Assert.Equal(ValidationSeverity.Medium, mc5!.Severity);
        Assert.Contains("0x7E8", mc5.Message);
    }

    [Fact]
    public void DistinctUdsIds_NoChannelConfigIssue()
    {
        var suite = SuiteWith(Bus("bus-a", 0x7E0, 0x7E8), Bus("bus-b", 0x6E0, 0x6E8));

        var issues = NewRegistry().Validate(suite);

        Assert.DoesNotContain(issues, i => i.RuleId == "MC-3");
        Assert.DoesNotContain(issues, i => i.RuleId == "MC-4");
        Assert.DoesNotContain(issues, i => i.RuleId == "MC-5");
    }
}
