using System;

namespace PeakCan.Host.Core.Path;

// NOTE: This file lives in namespace PeakCan.Host.Core.Path which shadows
// System.IO.Path under ImplicitUsings. All Path.* calls must use the
// fully-qualified System.IO.Path to avoid resolving to this namespace.

/// <summary>
/// Defense-in-depth path validation. Rejects relative paths, traversal segments (`..`),
/// null bytes, and null/empty input. Returns canonical absolute path on success.
/// Does NOT restrict to specific root directories (deferred to v1.6.0 — see ADR-1).
/// </summary>
public static class PathNormalizer
{
    public static string Normalize(string? path)
    {
        if (path is null)
        {
            throw new PathNormalizationException(
                "Path is null.",
                path: "",
                reason: PathNormalizationReason.NullPath);
        }

        if (path.Length == 0)
        {
            throw new PathNormalizationException(
                "Path is empty.",
                path: path,
                reason: PathNormalizationReason.EmptyPath);
        }

        if (path.Contains('\0'))
        {
            throw new PathNormalizationException(
                $"Path contains null byte at index {path.IndexOf('\0')}.",
                path: path,
                reason: PathNormalizationReason.NullByte);
        }

        // Relative path: doesn't start with drive letter (C:) or UNC (\\).
        if (!System.IO.Path.IsPathRooted(path))
        {
            throw new PathNormalizationException(
                $"Path is not absolute (must start with drive letter or \\\\): {path}",
                path: path,
                reason: PathNormalizationReason.RelativePath);
        }

        // Pre-canonicalization traversal check. Catches attack intent even when
        // `..` resolves cleanly under GetFullPath (e.g. C:\foo\..\..\etc\passwd
        // canonicalizes to D:\etc\passwd on the current drive). Splitting on
        // both separators catches POSIX-style traversal as well as Windows.
        if (ContainsTraversalSegment(path))
        {
            throw new PathNormalizationException(
                $"Path contains traversal segment '..': {path}",
                path: path,
                reason: PathNormalizationReason.TraversalSegment);
        }

        var canonical = System.IO.Path.GetFullPath(path);

        // Post-canonicalization traversal check. Catches unresolved `..` that
        // escapes the path root (e.g. C:\..\..\sensitive). Belt-and-suspenders
        // alongside the pre-canonicalization check above.
        if (ContainsTraversalSegment(canonical))
        {
            throw new PathNormalizationException(
                $"Path contains traversal segment '..' after canonicalization: {canonical}",
                path: path,
                reason: PathNormalizationReason.TraversalSegment);
        }

        return canonical;
    }

    private static bool ContainsTraversalSegment(string path)
    {
        var separators = new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar };
        var segments = path.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        return Array.IndexOf(segments, "..") >= 0;
    }
}
