namespace PeakCan.Host.Mobile.Core.Chat;

/// <summary>Metadata for one saved chat key (no secrets).</summary>
/// <param name="CredentialKey">Credential-store key, <c>PeakCan/{provider}/{alias}</c>.</param>
/// <param name="Provider">Vendor display name (DeepSeek / GLM / Kimi / 自定义).</param>
/// <param name="Alias">User-chosen key alias.</param>
/// <param name="ApiBase">API base URL (custom vendors need it restored across restarts).</param>
/// <param name="Model">Model name for this key.</param>
public sealed record SavedChatKeyMeta(
    string CredentialKey,
    string Provider,
    string Alias,
    string ApiBase,
    string Model);

/// <summary>
/// Persistence for saved chat key <b>metadata</b>. The API key secret itself
/// lives in <see cref="PeakCan.HIL.Core.Analysis.ICredentialStore"/>; this store
/// only remembers vendor/api-base/model so custom vendors survive restarts.
/// Implemented in the MAUI layer over <c>Preferences</c>.
/// </summary>
public interface IChatConfigStore
{
    /// <summary>All saved key metadata (empty when none).</summary>
    IReadOnlyList<SavedChatKeyMeta> Load();

    /// <summary>Replace the full metadata list (the list is small, so a
    /// whole-write is simpler than incremental edits).</summary>
    void Save(IReadOnlyList<SavedChatKeyMeta> keys);
}
