using System;

namespace PeakCan.Host.Core.Path;

/// <summary>
/// Thrown when a file-system path is invalid per <see cref="PathNormalizer"/> rules
/// (relative, contains traversal segments, contains null bytes, or is null/empty).
/// </summary>
public sealed class PathNormalizationException : ArgumentException
{
    public string? AttemptedPath { get; }
    public PathNormalizationReason Reason { get; }

    public PathNormalizationException(string message, string? attemptedPath, PathNormalizationReason reason)
        : base(message)
    {
        AttemptedPath = attemptedPath;
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