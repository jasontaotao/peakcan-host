using System.Text.Json;

namespace PeakCan.Host.Core.Services;

/// <summary>W37 god-class refactor (22nd overall): search-dirs cache +
/// load + default path extracted from main. Sister of W34 DbcSendViewModel/
/// subdirectory pattern. The 3 methods form a coupled responsibility:
/// GetSearchDirs (lazy-init with double-checked locking) delegates to
/// LoadSearchDirsFromDisk (reads %APPDATA%/PeakCan.Host/asc-search-dirs.json)
/// which uses DefaultSearchDirsPath for the fallback path. Keeping all 3
/// in the same partial avoids cross-partial state exposure.</summary>
public sealed partial class FileSystemAscLocator
{
    /// <summary>Double-checked locking lazy init. Returns the cached
    /// list on subsequent calls; loads from disk on first access.
    /// Sister of sister pattern in v3.6.4 PATCH.</summary>
    private List<string> GetSearchDirs()
    {
        if (_cachedDirs is not null) return _cachedDirs;
        lock (_cacheGate)
        {
            if (_cachedDirs is not null) return _cachedDirs;
            _cachedDirs = LoadSearchDirsFromDisk();
            return _cachedDirs;
        }
    }

    /// <summary>Reads JSON array of absolute directory paths from
    /// <see cref="SearchDirsPath"/>. Missing or corrupt file → empty
    /// list (locator returns null → caller falls back to path-only
    /// resolution).</summary>
    private List<string> LoadSearchDirsFromDisk()
    {
        try
        {
            if (!File.Exists(SearchDirsPath)) return new List<string>();
            var json = File.ReadAllText(SearchDirsPath);
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            var dirs = JsonSerializer.Deserialize<List<string>>(json);
            return dirs ?? new List<string>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogConfigCorrupt(_logger, ex, SearchDirsPath);
            return new List<string>();
        }
    }

    /// <summary>Default path to the user-known search dirs JSON file.
    /// <c>%APPDATA%/PeakCan.Host/asc-search-dirs.json</c> per v3.6.4 PATCH.</summary>
    private static string DefaultSearchDirsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, "PeakCan.Host", "asc-search-dirs.json");
    }
}