namespace PeakCan.HIL.Core.HIL;

/// <summary>
/// Result of a test suite execution.
/// </summary>
public sealed record TestSuiteResult(
    string SuiteName,
    int TotalCases,
    int PassedCases,
    int FailedCases,
    int SkippedCases,
    int ElapsedMs,
    IReadOnlyList<string> SetupFailures,
    IReadOnlyList<TestCaseResult> CaseResults)
{
    public double PassRate => TotalCases > 0 ? (double)PassedCases / TotalCases : 0.0;

    public bool AllPassed => TotalCases > 0 && FailedCases == 0 && SkippedCases == 0;
}
