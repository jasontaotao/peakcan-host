using System.Xml.Linq;
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.Cli;

/// <summary>
/// Writes test suite results to JUnit XML format.
/// </summary>
public static class JUnitWriter
{
    public static async Task WriteJunit(TestSuiteResult result, string path)
    {
        var testCases = new List<XElement>();
        foreach (var cr in result.CaseResults)
        {
            XElement? failure = null;
            if (!cr.Passed)
            {
                var stepDetails = string.Join("\n", cr.StepResults
                    .Where(r => r.Status == StepStatus.Failed)
                    .Select(r => $"Step {r.StepIndex}: {r.Message}"));
                failure = new XElement("failure",
                    new XAttribute("message", cr.FailureReason ?? ""),
                    stepDetails);
            }

            testCases.Add(new XElement("testcase",
                new XAttribute("name", cr.TestCaseName),
                new XAttribute("classname", result.SuiteName),
                new XAttribute("time", $"{cr.ElapsedMs / 1000.0:F3}"),
                failure));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("testsuites",
                new XElement("testsuite",
                    new XAttribute("name", result.SuiteName),
                    new XAttribute("tests", result.TotalCases),
                    new XAttribute("failures", result.FailedCases),
                    new XAttribute("skipped", result.SkippedCases),
                    new XAttribute("time", $"{result.ElapsedMs / 1000.0:F3}"),
                    testCases)));

        await using var stream = File.Create(path);
        await doc.SaveAsync(stream, SaveOptions.None, CancellationToken.None);
    }
}
