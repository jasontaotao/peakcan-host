using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Analysis;
using PeakCan.HIL.Core.HIL.Expressions;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.Multichannel;

/// <summary>
/// TargetChannelValidator (Q3) tests — StepValidatorRegistry.Validate(suite) 的通道引用校验：
/// (a) suite 无 Channels + 步骤带 TargetChannel → Critical (MC-1)
/// (b) TargetChannel 引用未声明的通道名 → Critical (MC-2)
/// 单通道 suite（无 Channels、无 TargetChannel）零变化（无 issue）。
/// </summary>
public sealed class TargetChannelValidatorTests
{
    private static readonly ExpressionEvaluator Evaluator = new();

    private static StepValidatorRegistry NewRegistry() => new(Evaluator, dbcLookup: null);

    private static TestCase CaseWithSendFrame(string caseId, string? targetChannel)
    {
        var p = new SendFrameStep(new CanId(0x123, FrameFormat.Standard), new byte[] { 0x01 }, false, false)
        {
            TargetChannel = targetChannel,
        };
        var step = TestCaseStep.Create(p, label: "send");
        return new TestCase(caseId, caseId, "desc", null,
            new[] { step }, null, Array.Empty<string>());
    }

    [Fact]
    public void SuiteWithoutChannels_AndStepWithTargetChannel_ReportsCritical()
    {
        // Arrange: 无 Channels 声明，但步骤带 TargetChannel="bus-a"
        var suite = new TestSuite("s", new[] { CaseWithSendFrame("c1", "bus-a") },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig());

        // Act
        var issues = NewRegistry().Validate(suite);

        // Assert: MC-1 Critical
        var mc1 = issues.FirstOrDefault(i => i.RuleId == "MC-1");
        Assert.NotNull(mc1);
        Assert.Equal(ValidationSeverity.Critical, mc1!.Severity);
        Assert.Contains("bus-a", mc1.Message);
        Assert.Contains("no Channels", mc1.Message);
    }

    [Fact]
    public void SuiteWithChannels_AndStepReferencesUndeclaredChannel_ReportsCritical()
    {
        // Arrange: 声明了 bus-a，但步骤引用 bus-b（未声明）
        var channels = new[]
        {
            new ChannelConfig("bus-a", "USB1", BaudRate.Can500kbps, false, null, null, null),
        };
        var suite = new TestSuite("s", new[] { CaseWithSendFrame("c1", "bus-b") },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), Channels: channels);

        // Act
        var issues = NewRegistry().Validate(suite);

        // Assert: MC-2 Critical
        var mc2 = issues.FirstOrDefault(i => i.RuleId == "MC-2");
        Assert.NotNull(mc2);
        Assert.Equal(ValidationSeverity.Critical, mc2!.Severity);
        Assert.Contains("bus-b", mc2.Message);
    }

    [Fact]
    public void SuiteWithChannels_AndStepReferencesDeclaredChannel_NoChannelIssue()
    {
        // Arrange: 声明 bus-a，步骤引用 bus-a（合法）
        var channels = new[]
        {
            new ChannelConfig("bus-a", "USB1", BaudRate.Can500kbps, false, null, null, null),
            new ChannelConfig("bus-b", "USB2", BaudRate.Can500kbps, false, null, null, null),
        };
        var suite = new TestSuite("s", new[] { CaseWithSendFrame("c1", "bus-b") },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), Channels: channels);

        // Act
        var issues = NewRegistry().Validate(suite);

        // Assert: 无 MC-1 / MC-2
        Assert.DoesNotContain(issues, i => i.RuleId == "MC-1");
        Assert.DoesNotContain(issues, i => i.RuleId == "MC-2");
    }

    [Fact]
    public void SingleChannelSuite_NoTargetChannel_NoChannelIssue()
    {
        // Arrange: 无 Channels、步骤无 TargetChannel（单通道零回归场景）
        var suite = new TestSuite("s", new[] { CaseWithSendFrame("c1", targetChannel: null) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig());

        // Act
        var issues = NewRegistry().Validate(suite);

        // Assert: 无 MC-1 / MC-2（单通道行为零变化）
        Assert.DoesNotContain(issues, i => i.RuleId == "MC-1");
        Assert.DoesNotContain(issues, i => i.RuleId == "MC-2");
    }

    [Fact]
    public void NullTargetChannel_TreatedAsDefault_NoIssueEvenWithoutChannels()
    {
        // Arrange: 无 Channels，但步骤 TargetChannel=null（=默认通道，合法）
        var suite = new TestSuite("s", new[] { CaseWithSendFrame("c1", targetChannel: null) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig());

        var issues = NewRegistry().Validate(suite);
        Assert.DoesNotContain(issues, i => i.RuleId == "MC-1");
    }
}
