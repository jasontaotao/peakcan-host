namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Application-wide current DBC for playback and browse pages.</summary>
public sealed class DbcCatalogHolder
{
    private DbcCatalog? _current;

    /// <summary>Raised after every Set so open pages can hot-apply the new catalog.</summary>
    public event Action? Changed;

    public DbcCatalog? Current => _current;

    public void Set(DbcCatalog? catalog)
    {
        _current = catalog;
        Changed?.Invoke();
    }
}
