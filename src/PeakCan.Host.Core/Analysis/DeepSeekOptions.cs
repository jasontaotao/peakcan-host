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
}
