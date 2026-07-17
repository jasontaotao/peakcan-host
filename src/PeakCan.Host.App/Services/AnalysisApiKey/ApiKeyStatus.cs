namespace PeakCan.Host.App.Services.AnalysisApiKey;

/// <summary>
/// W40 P2 PATCH: status of the DeepSeek API key as exposed to the
/// AI Analysis panel. The key value itself is NEVER included —
/// only metadata about whether it is configured and any recent
/// error/operation timestamp.
/// </summary>
/// <param name="State">Configured or not.</param>
/// <param name="LastError">
/// Optional error message from the most recent operation (check /
/// set / remove). Null when no error.
/// </param>
/// <param name="LastUpdatedAt">
/// UTC timestamp of the most recent set operation. Null until the
/// first set; preserved across removes.
/// </param>
public sealed record ApiKeyStatus(
    ApiKeyConfiguredState State,
    string? LastError = null,
    DateTimeOffset? LastUpdatedAt = null);