namespace PeakCan.HIL.Core.HIL;

/// <summary>
/// Progress report emitted during TestSuite execution.
/// </summary>
public sealed record TestProgress(
    int CompletedCases,
    int TotalCases,
    string? CurrentCaseName = null,
    string? Message = null)
{
    public double PercentComplete => TotalCases > 0
        ? (double)CompletedCases / TotalCases * 100
        : 0;
}
