using System.Xml.Linq;
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.Cli;

/// <summary>
/// Writes test suite results to TRX format.
/// </summary>
public static class ResultWriter
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/2010/testtools";

    public static async Task WriteTrx(TestSuiteResult result, string path)
    {
        var doc = new XDocument(
            new XElement(Ns + "TestRun",
                new XAttribute("id", Guid.NewGuid().ToString()),
                new XAttribute("name", result.SuiteName),
                new XElement(Ns + "Results",
                    result.CaseResults.Select(cr =>
                        new XElement(Ns + "UnitTestResult",
                            new XAttribute("testId", Guid.NewGuid().ToString()),
                            new XAttribute("testName", cr.TestCaseName),
                            new XAttribute("outcome", cr.Passed ? "Passed" : "Failed"),
                            new XAttribute("duration", FormatDuration(cr.ElapsedMs))))),
                new XElement(Ns + "TestDefinitions",
                    result.CaseResults.Select(cr =>
                        new XElement(Ns + "UnitTest",
                            new XAttribute("id", Guid.NewGuid().ToString()),
                            new XAttribute("name", cr.TestCaseName),
                            new XElement(Ns + "Execution",
                                new XAttribute("id", Guid.NewGuid().ToString())))))));

        await using var stream = File.Create(path);
        await doc.SaveAsync(stream, SaveOptions.None, CancellationToken.None);
    }

    private static string FormatDuration(int elapsedMs)
    {
        var ts = TimeSpan.FromMilliseconds(elapsedMs);
        return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }
}
