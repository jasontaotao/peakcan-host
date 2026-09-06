using System.Collections.Concurrent;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Process-wide cache for parsed <see cref="DbcDocument"/>s, keyed by
/// (absolute path, LastWriteTimeUtc ticks, length). P0-3 (2026-09-06):
/// HilRunnerService rebuilds the headless host per run; without this cache
/// every run re-reads and re-parses the same DBC file (large OEM DBCs can
/// take hundreds of ms to seconds). Sharing instances across hosts is safe
/// because DbcDocument is treated as read-only after parse (no mutation
/// sites in the codebase — verified 2026-09-06).
/// <para>
/// Stamp validation means an on-disk change invalidates the entry
/// automatically; a failed re-parse keeps the stale entry out of the way
/// (the exception propagates, next call retries from disk).
/// </para>
/// </summary>
internal static class DbcDocumentCache
{
    /// <summary>
    /// Cap on concurrently cached documents (parsed DBCs can be large).
    /// Simple eviction: when over the cap, drop arbitrary entries (the
    /// common workflow touches only 1-3 DBCs per session, so the cap is
    /// effectively never hit in practice).
    /// </summary>
    private const int MaxEntries = 8;

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct CacheEntry((long LastWriteTimeUtcTicks, long Length) Stamp, DbcDocument Document);

    /// <summary>
    /// Load and parse <paramref name="path"/>, serving a cached document when
    /// the file stamp is unchanged. Throws <see cref="InvalidOperationException"/>
    /// when parsing fails (same contract as the inline parse it replaces).
    /// </summary>
    public static DbcDocument Load(string path)
    {
        var key = Path.GetFullPath(path);
        var fi = new FileInfo(path);
        if (!fi.Exists)
            throw new FileNotFoundException($"DBC file not found: '{path}'", path);
        var stamp = (fi.LastWriteTimeUtc.Ticks, fi.Length);
        if (Cache.TryGetValue(key, out var entry) && entry.Stamp == stamp)
            return entry.Document;

        var text = File.ReadAllText(path);
        var parsed = DbcParser.Parse(text);
        if (!parsed.IsSuccess)
            throw new InvalidOperationException($"DBC parse failed for '{path}': {parsed.Error?.Message}");
        var doc = parsed.Value!;
        Cache[key] = new CacheEntry(stamp, doc);
        EvictIfOverCap();
        return doc;
    }

    /// <summary>Test hook: clears all cached entries.</summary>
    internal static void Clear() => Cache.Clear();

    private static void EvictIfOverCap()
    {
        while (Cache.Count > MaxEntries)
        {
            foreach (var key in Cache.Keys)
            {
                Cache.TryRemove(key, out _);
                break;
            }
        }
    }
}
