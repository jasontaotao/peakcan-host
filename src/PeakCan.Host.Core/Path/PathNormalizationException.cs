using System;

namespace PeakCan.Host.Core.Path;

/// <summary>
/// Thrown when a file-system path is invalid per <see cref="PathNormalizer"/> rules
/// (relative, contains traversal segments, contains null bytes, is null/empty, or
/// — via <see cref="PathNormalizer.NormalizeRestricted(string?, IReadOnlyCollection{string})"/> —
/// is outside the caller's allowed-root allowlist).
/// </summary>
public sealed class PathNormalizationException : ArgumentException
{
    /// <summary>
    /// The path that was attempted. Empty string (<c>""</c>) when no path was
    /// attempted (e.g. <see cref="PathNormalizationReason.NullPath"/>).
    /// </summary>
    public string Path { get; }
    public PathNormalizationReason Reason { get; }

    public PathNormalizationException(string message, string path, PathNormalizationReason reason)
        : base(message)
    {
        Path = path;
        Reason = reason;
    }
}

public enum PathNormalizationReason
{
    NullPath,
    EmptyPath,
    RelativePath,
    TraversalSegment,
    NullByte,
    /// <summary>
    /// (v1.6.4 PATCH) The path canonicalizes successfully but does not start
    /// with any of the allowed roots passed to
    /// <see cref="PathNormalizer.NormalizeRestricted(string?, IReadOnlyCollection{string})"/>.
    /// </summary>
    OutsideAllowedRoot,
}
