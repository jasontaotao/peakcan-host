using System.Xml.Linq;
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.Cli.Tests;

public class JUnitWriterTests
{
    private static TestCaseResult MakeCase(string name, bool passed, string? failureReason = null,
        StepResult[]? stepResults = null)
    {
        return new TestCaseResult(
            TestCaseId: name,
            TestCaseName: name,
            Passed: passed,
            FailureReason: failureReason,
            ElapsedMs: 500,
            TotalSteps: stepResults?.Length ?? 0,
            PassedSteps: stepResults?.Count(s => s.Passed) ?? 0,
            FailedSteps: stepResults?.Count(s => !s.Passed) ?? 0,
            SkippedSteps: 0,
            CommentSteps: 0,
            StepResults: stepResults ?? Array.Empty<StepResult>());
    }

    private static StepResult MakeStep(int index, bool passed, string message)
    {
        return new StepResult(index, TestCaseStepKind.AssertSignal, null,
            passed ? StepStatus.Passed : StepStatus.Failed,
            message, null, null, 0);
    }

    [Fact]
    public async Task WriteJunit_ValidSuite_ProducesValidXml()
    {
        // Arrange
        var result = new TestSuiteResult(
            SuiteName: "IntegrationSuite",
            TotalCases: 2,
            PassedCases: 1,
            FailedCases: 1,
            SkippedCases: 0,
            ElapsedMs: 1500,
            SetupFailures: Array.Empty<string>(),
            CaseResults: new[]
            {
                MakeCase("case_1", passed: true),
                MakeCase("case_2", passed: false, failureReason: "Step 0 failed: signal RPM out of tolerance",
                    stepResults: new[] { MakeStep(0, false, "signal RPM out of tolerance"), MakeStep(1, true, "ok") })
            });

        var path = Path.GetTempFileName();

        try
        {
            // Act
            await JUnitWriter.WriteJunit(result, path);
            var doc = XDocument.Load(path);

            // Assert
            var testsuites = doc.Root!;
            var testsuite = testsuites.Element("testsuite")!;
            Assert.Equal("2", testsuite.Attribute("tests")!.Value);
            Assert.Equal("1", testsuite.Attribute("failures")!.Value);
            Assert.Equal("IntegrationSuite", testsuite.Attribute("name")!.Value);

            var cases = testsuite.Elements("testcase").ToList();
            Assert.Equal(2, cases.Count);

            // Passed case has no failure element
            var passCase = cases[0];
            Assert.Null(passCase.Element("failure"));

            // Failed case has failure element with step details (only failed steps)
            var failCase = cases[1];
            var failure = failCase.Element("failure")!;
            Assert.Contains("Step 0:", failure.Value);
            // Step 1 passed, so it should NOT appear in failure details
            Assert.DoesNotContain("Step 1:", failure.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteJunit_EmptySuite_OutputsZeroTests()
    {
        // Arrange
        var result = new TestSuiteResult(
            SuiteName: "EmptySuite",
            TotalCases: 0,
            PassedCases: 0,
            FailedCases: 0,
            SkippedCases: 0,
            ElapsedMs: 0,
            SetupFailures: Array.Empty<string>(),
            CaseResults: Array.Empty<TestCaseResult>());

        var path = Path.GetTempFileName();

        try
        {
            // Act
            await JUnitWriter.WriteJunit(result, path);
            var doc = XDocument.Load(path);

            // Assert
            var testsuite = doc.Root!.Element("testsuite")!;
            Assert.Equal("0", testsuite.Attribute("tests")!.Value);
            Assert.Equal("0", testsuite.Attribute("failures")!.Value);
            Assert.Equal("0", testsuite.Attribute("skipped")!.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteJunit_TimeFormattedAsSeconds()
    {
        // Arrange
        var result = new TestSuiteResult(
            SuiteName: "TimeSuite",
            TotalCases: 1,
            PassedCases: 1,
            FailedCases: 0,
            SkippedCases: 0,
            ElapsedMs: 1500,
            SetupFailures: Array.Empty<string>(),
            CaseResults: new[] { MakeCase("case_1", passed: true) });

        var path = Path.GetTempFileName();

        try
        {
            // Act
            await JUnitWriter.WriteJunit(result, path);
            var doc = XDocument.Load(path);

            // Assert
            var testsuite = doc.Root!.Element("testsuite")!;
            Assert.Equal("1.500", testsuite.Attribute("time")!.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteJunit_FailureMessageContainsStepDetails()
    {
        // Arrange
        var result = new TestSuiteResult(
            SuiteName: "DetailSuite",
            TotalCases: 1,
            PassedCases: 0,
            FailedCases: 1,
            SkippedCases: 0,
            ElapsedMs: 500,
            SetupFailures: Array.Empty<string>(),
            CaseResults: new[]
            {
                MakeCase("case_1", passed: false, failureReason: "Step 0 failed: signal RPM out of tolerance",
                    stepResults: new[]
                    {
                        MakeStep(0, false, "signal RPM out of tolerance"),
                        MakeStep(1, false, "signal Temp out of tolerance")
                    })
            });

        var path = Path.GetTempFileName();

        try
        {
            // Act
            await JUnitWriter.WriteJunit(result, path);
            var doc = XDocument.Load(path);

            // Assert
            var failure = doc.Root!.Element("testsuite")!.Element("testcase")!.Element("failure")!;
            Assert.Contains("Step 0: signal RPM out of tolerance", failure.Value);
            Assert.Contains("Step 1: signal Temp out of tolerance", failure.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
