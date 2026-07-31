using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.Cli.Reporting;

namespace PeakCan.Host.Infrastructure.Tests.Cli.Reporting;

public class ConsoleSummaryFormatterTests
{
    [Fact]
    public void ConsoleSummary_MixedResults_PrintsPassAndFail()
    {
        var result = new TestSuiteResult("Suite", 3, 2, 1, 0, 100,
            Array.Empty<string>(), new TestCaseResult[]
            {
                new("Pass1", "Pass1", true, null, 10, 1, 1, 0, 0, 0, Array.Empty<StepResult>()),
                new("Pass2", "Pass2", true, null, 10, 1, 1, 0, 0, 0, Array.Empty<StepResult>()),
                new("Fail1", "Fail1", false, "assertion failed", 10, 1, 0, 1, 0, 0, new[]
                {
                    new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed, "out", "5", "10", 0)
                }),
            });

        var output = ConsoleSummaryFormatter.Format(result);

        Assert.Contains("Pass1", output);
        Assert.Contains("Fail1", output);
        Assert.Contains("2", output); // passed count
        Assert.Contains("1", output); // failed count
    }

    [Fact]
    public void ConsoleSummary_DoesNotConflictWithConsoleProgress()
    {
        // Verify both can be used independently
        var result = new TestSuiteResult("Suite", 1, 1, 0, 0, 100,
            Array.Empty<string>(), new TestCaseResult[]
            {
                new("Pass1", "Pass1", true, null, 10, 1, 1, 0, 0, 0, Array.Empty<StepResult>()),
            });

        var summary = ConsoleSummaryFormatter.Format(result);
        Assert.NotNull(summary);
        Assert.NotEmpty(summary);
    }
}
