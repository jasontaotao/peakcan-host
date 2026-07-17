namespace PeakCan.Host.App.Services.AnalysisApiKey;

/// <summary>
/// W40 P2 PATCH: configured/not-configured state for the DeepSeek
/// API key. Two-state enum — any present/non-empty value counts as
/// configured; null or empty counts as not set.
/// </summary>
public enum ApiKeyConfiguredState
{
    /// <summary>Credential store returned null or empty for the key.</summary>
    NotSet,
    /// <summary>Credential store returned a non-empty value for the key.</summary>
    Configured,
}