using System.IO;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Services.Uds;

/// <summary>
/// ASC/BLF loader for the offline UDS analysis page. Rejects missing files,
/// oversized files and unknown extensions with a user-readable
/// <see cref="UdsOfflineLoadException"/> instead of letting a parser exception
/// or an empty frame list stand in for "analysis complete".
/// </summary>
public sealed class UdsOfflineTraceLoader(ReplayOptions? options = null) : IUdsOfflineTraceLoader
{
    private readonly ReplayOptions _options = options ?? ReplayOptions.Default;

    public async Task<UdsOfflineTraceLoadResult> LoadAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new UdsOfflineLoadException("未选择日志文件。");
        if (!File.Exists(path))
            throw new UdsOfflineLoadException($"文件不存在：{path}");

        var length = new FileInfo(path).Length;
        if (length > _options.MaxFileSizeBytes)
            throw new UdsOfflineLoadException(
                $"文件 {length:N0} 字节超过上限 {_options.MaxFileSizeBytes:N0} 字节（默认上限 200 MB）。");

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".asc" or ".blf"))
            throw new UdsOfflineLoadException($"不支持的日志格式：{extension}（仅支持 .asc / .blf）");

        await using var stream = File.OpenRead(path);
        return extension switch
        {
            ".asc" => new(await AscParser.ParseAsync(stream, _options, logger: null, ct).ConfigureAwait(false), "ASC"),
            _ => new(await BlfParser.ParseAsync(stream, _options, logger: null, ct).ConfigureAwait(false), "BLF"),
        };
    }
}
