using System;

namespace PeakCan.Host.Core.Path;

// NOTE: This file lives in namespace PeakCan.Host.Core.Path which shadows
// System.IO.Path under ImplicitUsings. All Path.* calls must use the
// fully-qualified System.IO.Path to avoid resolving to this namespace.

/// <summary>
/// Defense-in-depth path validation. Rejects relative paths, traversal segments (`..`),
/// null bytes, and null/empty input. Returns canonical absolute path on success.
/// <para>
/// v1.6.4 PATCH adds <see cref="NormalizeRestricted(string?, IReadOnlyCollection{string})"/>
/// for callers that need allowlist enforcement on top of defense-in-depth
/// (closes v1.5.0 MINOR ADR-1 deferred root-check).
/// </para>
/// </summary>
public static class PathNormalizer
{
    /// <summary>
    /// Default-path root for default-path callers (UDS DidDatabase + RoutineDatabase
    /// JSON files). Equivalent to <c>%LOCALAPPDATA%\PeakCan.Host</c>. Computed once
    /// per process via <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
    /// + <see cref="System.IO.Path.Combine(string, string)"/>. Public so consumers
    /// in the same or other assemblies can reuse the allowlist value without
    /// duplicating the <c>Environment.SpecialFolder</c> + folder-name composition.
    /// </summary>
    public static string LocalAppDataPeakCanRoot { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeakCan.Host");

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

    /// <summary>
    /// Normalize with allowlist enforcement (v1.6.4 PATCH). Equivalent to
    /// <see cref="Normalize(string?)"/> plus a post-canonicalization prefix check:
    /// the resolved absolute path must start with one of
    /// <paramref name="allowedRoots"/> (case-insensitive, Windows path semantics).
    /// Default-path callers (UDS DidDatabase + RoutineDatabase) use this to enforce
    /// that the configured JSON DB always lives under
    /// <see cref="LocalAppDataPeakCanRoot"/>.
    /// <para>
    /// Defense-in-depth checks (null/empty/relative/traversal/null-byte) run first
    /// via <see cref="Normalize(string?)"/>; the allowlist check applies only if
    /// those pass. An empty <paramref name="allowedRoots"/> list rejects every
    /// path with <see cref="PathNormalizationReason.OutsideAllowedRoot"/> (safe
    /// default; callers must opt in by passing a non-empty allowlist).
    /// </para>
    /// </summary>
    /// <param name="path">Absolute path to validate.</param>
    /// <param name="allowedRoots">Non-null collection of allowed root prefixes.</param>
    /// <returns>Canonical absolute path on success.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="allowedRoots"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="PathNormalizationException">
    /// <see cref="PathNormalizationReason.OutsideAllowedRoot"/> if the canonical
    /// path does not start with any allowed root. Other reasons (null/empty/
    /// relative/traversal/null-byte) propagate from <see cref="Normalize(string?)"/>.
    /// </exception>
    public static string NormalizeRestricted(string? path, IReadOnlyCollection<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        var canonical = Normalize(path);   // defense-in-depth first
        foreach (var root in allowedRoots)
        {
            if (canonical.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return canonical;
        }
        throw new PathNormalizationException(
            $"Path '{canonical}' is outside the allowed roots " +
            $"[{string.Join(", ", allowedRoots)}].",
            path: path ?? string.Empty,
            reason: PathNormalizationReason.OutsideAllowedRoot);
    }

    private static bool ContainsTraversalSegment(string path)
    {
        var separators = new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar };
        var segments = path.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        return Array.IndexOf(segments, "..") >= 0;
    }
}
