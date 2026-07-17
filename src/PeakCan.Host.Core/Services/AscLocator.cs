using System.IO;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Services;

/// <summary>
/// v3.6.4 PATCH: relocate a missing <c>.asc</c> by content hash.
/// Given a SHA-256 hex string + a list of user-known search
/// directories, walks each directory recursively looking for
/// <c>.asc</c> files whose hash matches. The first match wins.
/// <para>
/// <b>Scope</b> (v3.6.4 PATCH): the user-known directory list comes
/// from <c>%APPDATA%/PeakCan.Host/asc-search-dirs.json</c> — a JSON
/// array of absolute directory paths the user edits manually. A future
/// MINOR can add a Settings UI; this PATCH keeps the surface minimal.
/// </para>
/// <para>
/// <b>Depth cap</b>: recursive walk is capped at
/// <see cref="FileSystemAscLocator.MaxSearchDepth"/> levels to bound
/// runtime on accidentally-pointed-at drives (e.g. <c>C:\</c>).
/// </para>
/// </summary>
public interface IAscLocator
{
    /// <summary>
    /// Search the configured user-known directories for an <c>.asc</c>
    /// file whose SHA-256 content hash matches <paramref name="contentHash"/>.
    /// Returns the absolute path of the first match, or <c>null</c>
    /// when no match is found or the search is cancelled. The empty
    /// string for <paramref name="contentHash"/> is a no-op (returns
    /// <c>null</c> immediately).
    /// </summary>
    Task<string?> LocateAsync(string contentHash, CancellationToken ct = default);
}

/// <summary>
/// v3.6.4 PATCH: default <see cref="IAscLocator"/> impl. Reads the
/// search-dir list lazily from <see cref="SearchDirsPath"/> on first
/// use; caches it for the lifetime of the instance. A missing or
/// corrupt config file is treated as an empty list — the locator
/// silently returns <c>null</c> and the existing path-only resolution
/// in <c>TraceViewerViewModel.ApplySnapshotAsync</c> continues to
/// work.
/// </summary>
public sealed partial class FileSystemAscLocator : IAscLocator
{
    /// <summary>Cap on recursive depth. Root + 3 subdir levels. Anything
    /// deeper would be a misconfigured search dir; stop early rather
    /// than walk a multi-GB tree.</summary>
    public const int MaxSearchDepth = 4;

    /// <summary>Path to the user-known search dirs JSON file. Default
    /// <c>%APPDATA%/PeakCan.Host/asc-search-dirs.json</c>. Test code
    /// may construct with an override path.</summary>
    public string SearchDirsPath { get; }

    private readonly ILogger<FileSystemAscLocator> _logger;
    private readonly IAscContentHasher _hasher;
    private readonly object _cacheGate = new();
    private List<string>? _cachedDirs;

    public FileSystemAscLocator(
        ILogger<FileSystemAscLocator> logger,
        IAscContentHasher hasher)
        : this(logger, hasher, DefaultSearchDirsPath()) { }

    /// <summary>Test ctor with explicit search-dirs path.</summary>
    internal FileSystemAscLocator(
        ILogger<FileSystemAscLocator> logger,
        IAscContentHasher hasher,
        string overridePath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        SearchDirsPath = overridePath ?? throw new ArgumentNullException(nameof(overridePath));
    }

    /// <inheritdoc />
    public async Task<string?> LocateAsync(string contentHash, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(contentHash)) return null;
        var dirs = GetSearchDirs();
        if (dirs.Count == 0) return null;

        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var found = await WalkAsync(dir, 0, contentHash, ct).ConfigureAwait(false);
            if (found is not null) return found;
        }
        return null;
    }
}
