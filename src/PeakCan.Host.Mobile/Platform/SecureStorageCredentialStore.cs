using PeakCan.HIL.Core.Analysis;

namespace PeakCan.Host.Mobile.Platform;

/// <summary>
/// <see cref="ICredentialStore"/> over MAUI <c>SecureStorage</c> (Android
/// Keystore-backed). API keys never touch Preferences or plaintext files.
/// </summary>
public sealed class SecureStorageCredentialStore : ICredentialStore
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new CredentialStoreException(key, "Failed to read from SecureStorage", ex);
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        try
        {
            await SecureStorage.Default.SetAsync(key, value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new CredentialStoreException(key, "Failed to write to SecureStorage", ex);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        try
        {
            SecureStorage.Default.Remove(key);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            throw new CredentialStoreException(key, "Failed to delete from SecureStorage", ex);
        }
    }
}
