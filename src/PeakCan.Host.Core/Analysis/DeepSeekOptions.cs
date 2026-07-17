namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.54.0 MINOR: configuration for the DeepSeek LLM provider.
/// Per v3.52.0 release notes: model id is NOT hardcoded as default
/// (DeepSeek-V4-Flash is not verified in official docs); user configures
/// the model id via DI Options. ApiBase default is the real DeepSeek
/// endpoint (sister of aspice-toolkit LLM_PROVIDERS.deepseek.endpoint).</summary>
public sealed record DeepSeekOptions
{
    public string ApiBase { get; init; } = "https://api.deepseek.com";
    public string Model { get; init; } = "deepseek-chat";
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// v3.59.0 MINOR: when true (default), <see cref="PeakCan.Host.App.Services.LlmProvider.DeepSeekProvider"/>
    /// uses SSE streaming via <c>stream: true</c>. When false, falls back
    /// to the v3.54.0 single-shot non-streaming path. Tests + callers with
    /// constrained network may opt out by setting this to false in DI.
    /// </summary>
    public bool UseStreaming { get; init; } = true;
}
