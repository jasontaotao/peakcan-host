using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.Platform;

public interface IDbcCatalogProvider
{
    Task<DbcCatalogLoadResult> PickAndLoadAsync(CancellationToken ct = default);
}
