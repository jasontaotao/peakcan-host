using PeakCan.HIL.Core.Analysis;

namespace PeakCan.Host.Infrastructure.HIL.Analysis;

/// <summary>
/// Sprint 17 Inc 3: composite <see cref="ICredentialStore"/> that reads across an
/// ordered list of backing stores (first non-null wins), writes only to the first
/// store, and deletes from all stores best-effort (per-store failures are swallowed
/// so one broken backend cannot prevent key removal from the others).
/// </summary>
public sealed class ChainedCredentialStore : ICredentialStore
{
    private readonly ICredentialStore[] _stores;

    public ChainedCredentialStore(params ICredentialStore[] stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        if (stores.Length == 0)
            throw new ArgumentException("At least one backing store is required.", nameof(stores));
        _stores = stores;
    }

    /// <summary>Return the first non-null value across stores, in order.</summary>
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        foreach (var store in _stores)
        {
            var value = await store.GetAsync(key, ct).ConfigureAwait(false);
            if (value is not null)
                return value;
        }
        return null;
    }

    /// <summary>Write to the primary store only (readers fall back to it first).</summary>
    public Task SetAsync(string key, string value, CancellationToken ct = default)
        => _stores[0].SetAsync(key, value, ct);

    /// <summary>Delete from all stores, continuing past per-store failures.</summary>
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        foreach (var store in _stores)
        {
            try
            {
                await store.DeleteAsync(key, ct).ConfigureAwait(false);
            }
            catch (CredentialStoreException)
            {
                // A platform failure on one backend must not block cleanup elsewhere.
            }
        }
    }
}
