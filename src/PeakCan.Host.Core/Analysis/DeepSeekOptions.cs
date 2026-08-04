namespace PeakCan.HIL.Core.Analysis;

/// <summary>v3.54.0 MINOR, updated 2026-07: configuration for the DeepSeek LLM provider.
/// Default model changed from "deepseek-chat" (deprecated 2026-07-24) to
/// "deepseek-v4-flash". See https://api-docs.deepseek.com for available models.</summary>
public sealed record DeepSeekOptions
{
    public string ApiBase { get; init; } = "https://api.deepseek.com";
    public string Model { get; init; } = "deepseek-v4-flash";
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// v3.59.0 MINOR: when true (default), <see cref="PeakCan.Host.App.Services.LlmProvider.DeepSeekProvider"/>
    /// uses SSE streaming via <c>stream: true</c>. When false, falls back
    /// to the v3.54.0 single-shot non-streaming path. Tests + callers with
    /// constrained network may opt out by setting this to false in DI.
    /// </summary>
    public bool UseStreaming { get; init; } = true;
}
