using System.Text.Json;
using PeakCan.HIL.Core.HIL.Serialization;

namespace PeakCan.Host.Infrastructure.Cli.Reporting;

/// <summary>
/// Manages the HIL trend JSON file (<c>./hil-trends.json</c>) with cross-process safety
/// via a named Mutex. Append-only ring buffer capped at <see cref="DefaultMaxEntries"/>.
/// </summary>
public static class TrendTracker
{
    /// <summary>Default trend file path (current working directory).</summary>
    public const string DefaultPath = "./hil-trends.json";

    /// <summary>Maximum number of entries retained (oldest removed first).</summary>
    public const int DefaultMaxEntries = 100;

    private const string MutexName = @"Global\hil-trends-mutex";
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Append a new trend entry. Removes oldest entries beyond <paramref name="maxEntries"/>.
    /// Thread-safe and cross-process safe via named Mutex.
    /// </summary>
    public static void Record(TrendEntry entry, string? path = null, int maxEntries = DefaultMaxEntries)
    {
        path ??= DefaultPath;

        var mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            try
            {
                mutex.WaitOne(MutexTimeout);
            }
            catch (AbandonedMutexException)
            {
                // Previous process crashed without releasing — we now own the mutex; continue.
            }

            var entries = LoadUnlocked(path);
            entries.Add(entry);

            // Trim oldest entries beyond cap
            while (entries.Count > maxEntries)
                entries.RemoveAt(0);

            SaveUnlocked(path, entries);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { /* best-effort */ }
            mutex.Dispose();
        }
    }

    /// <summary>
    /// Load all trend entries from disk. Returns empty list if file doesn't exist.
    /// Handles corrupt JSON by backing up the file and rebuilding from scratch.
    /// </summary>
    public static IReadOnlyList<TrendEntry> Load(string? path = null)
    {
        path ??= DefaultPath;

        var mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            try
            {
                mutex.WaitOne(MutexTimeout);
            }
            catch (AbandonedMutexException)
            {
                // Previous process crashed without releasing — continue with read access.
            }

            return LoadUnlocked(path);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { /* best-effort */ }
            mutex.Dispose();
        }
    }

    /// <summary>
    /// Internal load — caller must hold the mutex.
    /// </summary>
    private static List<TrendEntry> LoadUnlocked(string path)
    {
        if (!File.Exists(path))
            return new List<TrendEntry>();

        try
        {
            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<TrendEntry>>(json, HILJsonOptions.Default);
            return entries ?? new List<TrendEntry>();
        }
        catch (JsonException)
        {
            // Back up corrupt file with timestamp, then rebuild from scratch.
            var corruptPath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmss}";
            File.Copy(path, corruptPath, overwrite: true);
            return new List<TrendEntry>();
        }
    }

    /// <summary>
    /// Internal save — caller must hold the mutex.
    /// </summary>
    private static void SaveUnlocked(string path, List<TrendEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries, HILJsonOptions.Default);
        File.WriteAllText(path, json);
    }
}
