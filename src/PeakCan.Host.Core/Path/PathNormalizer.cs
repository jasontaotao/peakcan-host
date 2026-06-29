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
                attemptedPath: null,
                reason: PathNormalizationReason.NullPath);
        }

        if (path.Length == 0)
        {
            throw new PathNormalizationException(
                "Path is empty.",
                attemptedPath: path,
                reason: PathNormalizationReason.EmptyPath);
        }

        if (path.Contains('\0'))
        {
            throw new PathNormalizationException(
                $"Path contains null byte at index {path.IndexOf('\0')}.",
                attemptedPath: path,
                reason: PathNormalizationReason.NullByte);
        }

        // Relative path: doesn't start with drive letter (C:) or UNC (\\).
        if (!System.IO.Path.IsPathRooted(path))
        {
            throw new PathNormalizationException(
                $"Path is not absolute (must start with drive letter or \\\\): {path}",
                attemptedPath: path,
                reason: PathNormalizationReason.RelativePath);
        }

        // Pre-canonicalization traversal check. Catches attack intent even when
        // `..` resolves cleanly under GetFullPath (e.g. C:\foo\..\..\etc\passwd
        // canonicalizes to D:\etc\passwd on the current drive). Splitting on
        // both separators catches POSIX-style traversal as well as Windows.
        var inputSeparators = new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar };
        var inputSegments = path.Split(inputSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (Array.IndexOf(inputSegments, "..") >= 0)
        {
            throw new PathNormalizationException(
                $"Path contains traversal segment '..': {path}",
                attemptedPath: path,
                reason: PathNormalizationReason.TraversalSegment);
        }

        var canonical = System.IO.Path.GetFullPath(path);

        // Post-canonicalization traversal check. Catches unresolved `..` that
        // escapes the path root (e.g. C:\..\..\sensitive). Belt-and-suspenders
        // alongside the pre-canonicalization check above.
        var canonicalSeparators = new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar };
        var canonicalSegments = canonical.Split(canonicalSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (Array.IndexOf(canonicalSegments, "..") >= 0)
        {
            throw new PathNormalizationException(
                $"Path contains traversal segment '..' after canonicalization: {canonical}",
                attemptedPath: path,
                reason: PathNormalizationReason.TraversalSegment);
        }

        return canonical;
    }
}