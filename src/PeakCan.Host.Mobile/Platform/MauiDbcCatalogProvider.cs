using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Platform;

public sealed class MauiDbcCatalogProvider : IDbcCatalogProvider
{
    public async Task<DbcCatalogLoadResult> PickAndLoadAsync(CancellationToken ct = default)
    {
        var custom = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.Android] = ["application/octet-stream", "text/plain"],
        });

        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "选择 DBC 文件",
            FileTypes = custom,
        });
        if (result is null) return DbcCatalogLoadResult.Cancelled();

        if (!string.Equals(Path.GetExtension(result.FileName), ".dbc", StringComparison.OrdinalIgnoreCase))
            return new DbcCatalogLoadResult(null, result.FileName, "仅支持 .dbc 文件。");

        await using var stream = await result.OpenReadAsync();
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(ct);
        return DbcCatalog.Parse(text, result.FileName);
    }
}

