namespace PeakCan.Host.Core.Analysis;

/// <summary>
/// v3.59.0 MINOR: streaming progress notifications emitted by
/// <see cref="ILlmProvider.AnalyzeStreamingAsync"/>. Three variants:
/// <list type="bullet">
///   <item><see cref="PartialSummary"/> — incremental text fragment to
///         append to the running Summary (SSE <c>delta.content</c>).</item>
///   <item><see cref="PartialEvidenceId"/> — one Evidence ID surfaced
///         from the streaming response (e.g. "E-0017"); the consumer
///         accumulates these into the final whitelist-filtered set.</item>
///   <item><see cref="FinalResult"/> — terminal marker carrying the
///         completed <see cref="LlmAnalysisResult"/> after whitelist
///         filtering + 4096-char cap.</item>
/// </list>
/// Use pattern matching (`switch { PartialSummary s => ..., FinalResult r => ... }`)
/// to dispatch each variant.
/// </summary>
public abstract record LlmPartialUpdate
{
    private LlmPartialUpdate() { }

    /// <summary>Incremental Summary fragment to append.</summary>
    public sealed record PartialSummary(string Delta) : LlmPartialUpdate;

    /// <summary>One Evidence ID surfaced from the streaming response.</summary>
    public sealed record PartialEvidenceId(string EvidenceId) : LlmPartialUpdate;

    /// <summary>Terminal marker carrying the completed result.</summary>
    public sealed record FinalResult(LlmAnalysisResult Result) : LlmPartialUpdate;
}