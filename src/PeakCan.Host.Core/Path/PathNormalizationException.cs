using System;

namespace PeakCan.Host.Core.Path;

/// <summary>
/// Thrown when a file-system path is invalid per <see cref="PathNormalizer"/> rules
/// (relative, contains traversal segments, contains null bytes, or is null/empty).
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
}
