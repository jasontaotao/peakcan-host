using System.Text;
using PeakCan.Host.Mobile.Core.Platform;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>
/// Copies picked trace files into a private cache directory keyed by
/// (displayName, sizeBytes). Same name+size → cache hit, stream not
/// re-opened (saves the 100MB copy). Matches spec §5 “同文件重开秒开”.
/// </summary>
public sealed class TraceFileCache
{
    private readonly string _cacheDir;

    public TraceFileCache(string cacheDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDir);
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    public string? FindCached(string displayName, long sizeBytes)
    {
        var path = PathOf(displayName, sizeBytes);
        return File.Exists(path) ? path : null;
    }

    public async Task<string> ImportAsync(PickedTraceFile file, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var path = PathOf(file.DisplayName, file.SizeBytes);
        if (File.Exists(path)) return path; // cache hit — 不再读流

        var tmp = path + ".partial";
        await using (var src = await file.OpenReadAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(tmp))
        {
            var buffer = new byte[256 * 1024];
            long copied = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                copied += n;
                if (file.SizeBytes > 0) progress?.Report((double)copied / file.SizeBytes);
            }
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private string PathOf(string name, long size) =>
        Path.Combine(_cacheDir, $"{Sanitize(name)}.{size}.asc");

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }
}

