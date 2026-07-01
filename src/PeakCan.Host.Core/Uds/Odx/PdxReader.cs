using System.IO.Compression;
using System.Xml.Linq;

namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// Loads ODX <c>XDocument</c>s from a file path. Supports both:
/// <list type="bullet">
///   <item>Direct <c>.odx</c> files (single document).</item>
///   <item><c>.pdx</c> ZIP containers (multi-document cascade;
///         non-<c>.odx</c> entries silently skipped).</item>
/// </list>
/// </summary>
public sealed class PdxReader
{
    /// <summary>
    /// Load ODX documents from the given path. Throws on fatal
    /// I/O / parse errors; otherwise returns the parsed XDocument
    /// list (possibly with non-odx entries silently omitted in
    /// .pdx case).
    /// </summary>
    /// <exception cref="FileNotFoundException">Path does not exist.</exception>
    /// <exception cref="InvalidDataException">.pdx is not a valid ZIP.</exception>
    public async Task<IReadOnlyList<XDocument>> LoadAsync(
        string path,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"ODX path not found: {path}", path);

        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext == ".pdx"
            ? await LoadPdxAsync(path, ct).ConfigureAwait(false)
            : await LoadOdxAsync(path, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<XDocument>> LoadOdxAsync(
        string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var doc = await XDocument.LoadAsync(fs, LoadOptions.None, ct).ConfigureAwait(false);
        return new[] { doc };
    }

    private static async Task<IReadOnlyList<XDocument>> LoadPdxAsync(
        string path, CancellationToken ct)
    {
        var docs = new List<XDocument>();
        await using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.FullName.EndsWith(".odx", StringComparison.OrdinalIgnoreCase))
                continue;
            await using var s = entry.Open();
            var xdoc = await XDocument.LoadAsync(s, LoadOptions.None, ct).ConfigureAwait(false);
            docs.Add(xdoc);
        }
        return docs;
    }
}
