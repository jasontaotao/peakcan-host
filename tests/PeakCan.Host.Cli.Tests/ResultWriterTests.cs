using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Cli;
using Xunit;

namespace PeakCan.Host.Cli.Tests;

public class ResultWriterTests
{
    private static TestCaseResult CreateCaseResult(string name, bool passed, int elapsedMs = 1000)
        => new(name, name, passed, passed ? null : "fail reason", elapsedMs,
            1, passed ? 1 : 0, passed ? 0 : 1, 0, 0,
            Array.Empty<StepResult>());

    [Fact]
    public async Task WriteTrx_valid_result_produces_valid_XML()
    {
        var result = new TestSuiteResult("TestSuite", 2, 1, 1, 0, 2000,
            Array.Empty<string>(),
            new[] { CreateCaseResult("case_1", true), CreateCaseResult("case_2", false) });

        var path = Path.Combine(Path.GetTempPath(), $"hil_test_{Guid.NewGuid():N}.trx");
        try
        {
            await ResultWriter.WriteTrx(result, path);

            File.Exists(path).Should().BeTrue();
            var doc = XDocument.Load(path);
            doc.Root.Should().NotBeNull();
            doc.Root!.Name.LocalName.Should().Be("TestRun");
            doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult").Should().HaveCount(2);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteTrx_passed_case_outcome_Passed()
    {
        var result = new TestSuiteResult("S", 1, 1, 0, 0, 1000,
            Array.Empty<string>(), new[] { CreateCaseResult("c1", true) });

        var path = Path.Combine(Path.GetTempPath(), $"hil_test_{Guid.NewGuid():N}.trx");
        try
        {
            await ResultWriter.WriteTrx(result, path);
            var doc = XDocument.Load(path);
            var outcome = doc.Descendants().First(e => e.Name.LocalName == "UnitTestResult")
                .Attribute("outcome")!.Value;
            outcome.Should().Be("Passed");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteTrx_failed_case_outcome_Failed()
    {
        var result = new TestSuiteResult("S", 1, 0, 1, 0, 1000,
            Array.Empty<string>(), new[] { CreateCaseResult("c1", false) });

        var path = Path.Combine(Path.GetTempPath(), $"hil_test_{Guid.NewGuid():N}.trx");
        try
        {
            await ResultWriter.WriteTrx(result, path);
            var doc = XDocument.Load(path);
            var outcome = doc.Descendants().First(e => e.Name.LocalName == "UnitTestResult")
                .Attribute("outcome")!.Value;
            outcome.Should().Be("Failed");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteTrx_empty_suite_produces_valid_XML_with_no_results()
    {
        var result = new TestSuiteResult("Empty", 0, 0, 0, 0, 0,
            Array.Empty<string>(), Array.Empty<TestCaseResult>());

        var path = Path.Combine(Path.GetTempPath(), $"hil_test_{Guid.NewGuid():N}.trx");
        try
        {
            await ResultWriter.WriteTrx(result, path);
            var doc = XDocument.Load(path);
            doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult").Should().BeEmpty();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
