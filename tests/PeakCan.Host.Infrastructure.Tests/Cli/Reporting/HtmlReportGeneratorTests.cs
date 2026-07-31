using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;
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
}
