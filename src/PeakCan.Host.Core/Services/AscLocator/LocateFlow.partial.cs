using System.IO;

namespace PeakCan.Host.Core.Services;

/// <summary>W37 god-class refactor (22nd overall): Walk + Recurse
/// extracted from main. Sister of W34 DbcSendViewModel/SendFlow.partial.cs
/// pattern. The 2 methods are tightly coupled: RecurseAsync delegates
/// back to WalkAsync for each subdir. Keeping them in the same partial
/// avoids cross-partial state exposure.</summary>
public sealed partial class FileSystemAscLocator
{
    /// <summary>Recursive walk. Returns the first matching file's
    /// path, or <c>null</c> if nothing in this subtree matches.</summary>
    private async Task<string?> WalkAsync(
        string dir,
        int depth,
        string contentHash,
        CancellationToken ct)
    {
        if (depth >= MaxSearchDepth)
        {
            LogMaxDepthHit(_logger, dir, depth);
            return null;
        }
        // Match files at THIS level before recursing — keeps the search
        // hit-locality high (a moved .asc is usually still at a similar
        // tree depth).
        IEnumerable<string> files;
        IEnumerable<string> subdirs;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.asc", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Per-directory ACLs / transient errors: log + skip rather
            // than abort the entire search. The other configured roots
            // may still contain the recording.
            LogEnumerateFailed(_logger, ex, dir);
            files = Array.Empty<string>();
            subdirs = Array.Empty<string>();
            return await RecurseAsync(subdirs, depth, contentHash, ct).ConfigureAwait(false);
        }
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) return null;
            // Extension check is case-insensitive on Windows (NTFS)
            // but we double-check to be safe on case-sensitive filesystems.
            if (!file.EndsWith(".asc", StringComparison.OrdinalIgnoreCase)) continue;
            string? hash = null;
            try
            {
                hash = await _hasher.ComputeAsync(file, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                LogHashFailed(_logger, ex, file);
                continue;
            }
            if (string.Equals(hash, contentHash, StringComparison.OrdinalIgnoreCase))
            {
                LogFound(_logger, file);
                return file;
            }
        }
        try
        {
            subdirs = Directory.EnumerateDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogEnumerateFailed(_logger, ex, dir);
            return null;
        }
        return await RecurseAsync(subdirs, depth, contentHash, ct).ConfigureAwait(false);
    }

    private async Task<string?> RecurseAsync(
        IEnumerable<string> subdirs,
        int depth,
        string contentHash,
        CancellationToken ct)
    {
        foreach (var sub in subdirs)
        {
            if (ct.IsCancellationRequested) return null;
            var found = await WalkAsync(sub, depth + 1, contentHash, ct).ConfigureAwait(false);
            if (found is not null) return found;
        }
        return null;
    }
}