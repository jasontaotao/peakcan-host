using PeakCan.Host.Mobile.Core.Platform;

namespace PeakCan.Host.Mobile.Platform;

public sealed class MauiFilePickerGateway : IFilePickerGateway
{
    public string CacheDirectory => FileSystem.CacheDirectory;

    public async Task<PickedTraceFile?> PickTraceFileAsync(CancellationToken ct = default)
    {
        var custom = new FilePickerFileType(
            new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.Android] = new[] { "application/octet-stream", "text/plain" },
            });

        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "选择 trace 文件",
            FileTypes = custom,
        });
        if (result is null) return null;

        if (!string.Equals(Path.GetExtension(result.FileName), ".asc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("P1 仅支持 .asc 文件；.blf 将在后续版本支持。");

        await using var stream = await result.OpenReadAsync();
        var size = stream.Length;
        var name = result.FileName;
        return new PickedTraceFile(name, size, _ => result.OpenReadAsync());
    }
}
