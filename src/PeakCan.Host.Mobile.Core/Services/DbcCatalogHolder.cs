namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Application-wide current DBC for playback and browse pages.</summary>
public sealed class DbcCatalogHolder
{
    public DbcCatalog? Current { get; private set; }
    public void Set(DbcCatalog? catalog) => Current = catalog;
}
