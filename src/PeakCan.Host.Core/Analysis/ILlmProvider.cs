namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: LLM provider contract (interface only in P0).
/// P1 PATCH will add: DeepSeekProvider, AzureOpenAIProvider, LocalOllamaProvider.
/// All implementations MUST:
/// - Validate Evidence IDs against the input session (whitelist filter per
///   hard-boundary #13: drop invalid ID references AND their associated
///   claims; only reject whole response if all claims are invalid).
/// - NOT log Authorization headers or full response bodies.
/// - Surface 401/429/timeout/JSON-parse errors as LlmAnalysisResult.Error
///   (NOT exception), so the caller can show degraded local-only results.</summary>
public interface ILlmProvider
{
    string DisplayName { get; }
    Task<LlmAnalysisResult> AnalyzeAsync(AnalysisSession session, CancellationToken ct);
}

/// <summary>P0 stub. P1 PATCH will replace with concrete providers.</summary>
public sealed class NotImplementedLlmProvider : ILlmProvider
{
    public string DisplayName => "(no LLM — P0 local-only)";

    public Task<LlmAnalysisResult> AnalyzeAsync(
        AnalysisSession session, CancellationToken ct) =>
        throw new NotImplementedException(
            "P1 PATCH will implement LLM Provider; see ILlmProvider contract.");
}
