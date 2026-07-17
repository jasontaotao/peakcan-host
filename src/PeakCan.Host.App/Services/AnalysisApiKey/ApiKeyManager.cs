using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.App.Services.AnalysisApiKey;

/// <summary>
/// W40 P2 PATCH: thin wrapper around <see cref="ICredentialStore"/>
/// that exposes the DeepSeek API key configured-state to the WPF UI
/// without leaking the key value itself. The status record carries
/// only metadata (configured/not, last error, last update time) —
/// callers that need to use the key (e.g. <see cref="LlmProvider.DeepSeekProvider"/>)
/// read it directly via <see cref="ICredentialStore"/>, not through this helper.
/// </summary>
// W40 P2 PATCH: intentionally NOT sealed so NSubstitute can proxy it
// in test callsites (sister of v3.52.1 PATCH T3 unsealing
// EvidenceExtractor / LocalAnalyzer / AnalysisSessionRegistry for the
// same reason — Castle.DynamicProxy cannot proxy sealed types).
public class ApiKeyManager
{
    /// <summary>
    /// Credential key under which the DeepSeek API key is stored.
    /// Mirrors <c>DeepSeekProvider.ApiKeyCredentialKey</c> — keep in sync.
    /// </summary>
    public const string CredentialKey = "deepseek-api-key";

    private readonly ICredentialStore _store;
    private readonly ILogger<ApiKeyManager> _logger;
    private DateTimeOffset? _lastUpdatedAtUtc;

    // W40 P2 PATCH: parameterless ctor is required so NSubstitute can
    // proxy this class in test callsites (sister of v3.52.1 PATCH T3
    // unsealing rationale — Castle.DynamicProxy requires a parameterless
    // ctor on the proxied class). Production code paths use the (store,
    // logger) ctor below which throws on null. Tests that pass a
    // Substitute.For<ApiKeyManager>() never invoke CheckAsync/SetAsync/
    // RemoveAsync because the proxy intercepts every method call, so
    // the uninitialized _store/_logger never triggers an NRE.
    protected ApiKeyManager()
    {
        _store = null!;
        _logger = null!;
    }

    public ApiKeyManager(ICredentialStore store, ILogger<ApiKeyManager> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Probe whether the DeepSeek API key is configured in the
    /// credential store. Returns <see cref="ApiKeyConfiguredState.NotSet"/>
    /// when the store returns null or empty; <see cref="ApiKeyConfiguredState.Configured"/>
    /// otherwise. NEVER returns the key value itself.
    /// </summary>
    public async Task<ApiKeyStatus> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var value = await _store.GetAsync(CredentialKey, ct).ConfigureAwait(true);
            var state = string.IsNullOrEmpty(value)
                ? ApiKeyConfiguredState.NotSet
                : ApiKeyConfiguredState.Configured;
            return new ApiKeyStatus(state, LastError: null, LastUpdatedAt: _lastUpdatedAtUtc);
        }
        catch (CredentialStoreException ex)
        {
            _logger.LogWarning(ex, "Failed to read DeepSeek API key from credential store");
            return new ApiKeyStatus(
                ApiKeyConfiguredState.NotSet,
                LastError: ex.Message,
                LastUpdatedAt: _lastUpdatedAtUtc);
        }
    }

    /// <summary>
    /// Persist a new DeepSeek API key. Overwrites any existing value.
    /// NEVER logs the key value itself — only the operation.
    /// </summary>
    public async Task<ApiKeyStatus> SetAsync(string value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ApiKeyStatus(
                ApiKeyConfiguredState.NotSet,
                LastError: "API key is empty",
                LastUpdatedAt: _lastUpdatedAtUtc);
        }
        try
        {
            await _store.SetAsync(CredentialKey, value, ct).ConfigureAwait(true);
            _lastUpdatedAtUtc = DateTimeOffset.UtcNow;
            _logger.LogInformation("DeepSeek API key updated");
            return new ApiKeyStatus(
                ApiKeyConfiguredState.Configured,
                LastError: null,
                LastUpdatedAt: _lastUpdatedAtUtc);
        }
        catch (CredentialStoreException ex)
        {
            _logger.LogWarning(ex, "Failed to persist DeepSeek API key to credential store");
            return new ApiKeyStatus(
                ApiKeyConfiguredState.NotSet,
                LastError: ex.Message,
                LastUpdatedAt: _lastUpdatedAtUtc);
        }
    }

    /// <summary>
    /// Remove the DeepSeek API key from the credential store. No-op
    /// when the key was not configured. Updates the in-memory
    /// <c>_lastUpdatedAtUtc</c> only if a deletion actually happened.
    /// </summary>
    public async Task<ApiKeyStatus> RemoveAsync(CancellationToken ct = default)
    {
        try
        {
            // Probe first so we don't churn the LastUpdatedAt timestamp
            // when there was nothing to remove.
            var existing = await _store.GetAsync(CredentialKey, ct).ConfigureAwait(true);
            if (string.IsNullOrEmpty(existing))
            {
                return new ApiKeyStatus(
                    ApiKeyConfiguredState.NotSet,
                    LastError: null,
                    LastUpdatedAt: _lastUpdatedAtUtc);
            }
            await _store.DeleteAsync(CredentialKey, ct).ConfigureAwait(true);
            _logger.LogInformation("DeepSeek API key removed");
            return new ApiKeyStatus(
                ApiKeyConfiguredState.NotSet,
                LastError: null,
                LastUpdatedAt: _lastUpdatedAtUtc);
        }
        catch (CredentialStoreException ex)
        {
            _logger.LogWarning(ex, "Failed to remove DeepSeek API key from credential store");
            return new ApiKeyStatus(
                ApiKeyConfiguredState.NotSet,
                LastError: ex.Message,
                LastUpdatedAt: _lastUpdatedAtUtc);
        }
    }
}