namespace PeakCan.HIL.Core.HIL.Analysis;

/// <summary>
/// Sprint 14: Interface for LLM-assisted test failure analysis.
/// Defined in Core so CLI project can reference it.
/// </summary>
public interface IHilAnalysisService
{
    /// <summary>
    /// Analyze failed test cases and return a human-readable report.
    /// Returns null if analysis is unavailable (e.g., missing API key).
    /// </summary>
    Task<AnalysisResult?> AnalyzeAsync(TestSuiteResult result, CancellationToken ct = default);
}

/// <summary>
/// Result of LLM analysis.
/// </summary>
public sealed record AnalysisResult(
    string Content,
    bool IsUnavailable = false,
    string? UnavailableReason = null)
{
    public static AnalysisResult Unavailable(string reason) =>
        new(string.Empty, IsUnavailable: true, UnavailableReason: reason);

    public static AnalysisResult Success(string content) =>
        new(content);
}
