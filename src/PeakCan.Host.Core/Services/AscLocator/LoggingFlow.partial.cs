using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Services;

/// <summary>W37 god-class refactor (22nd overall): 5 [LoggerMessage] partials
/// extracted from main. Sister of W34 DbcSendViewModel/ subdirectory pattern
/// (D1: 3 partials). Each partial method logs one specific scenario
/// (max-depth hit / enumerate failed / hash failed / found / config corrupt).</summary>
public sealed partial class FileSystemAscLocator
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "AscLocator max-depth {Depth} hit at {Dir}; deeper subtrees skipped")]
    private static partial void LogMaxDepthHit(ILogger logger, string dir, int depth);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AscLocator enumerate failed for {Dir}")]
    private static partial void LogEnumerateFailed(ILogger logger, Exception ex, string dir);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AscLocator hash failed for {File}")]
    private static partial void LogHashFailed(ILogger logger, Exception ex, string file);

    [LoggerMessage(Level = LogLevel.Information, Message = "AscLocator found relocated .asc at {File}")]
    private static partial void LogFound(ILogger logger, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AscLocator search-dirs config unreadable: {Path}")]
    private static partial void LogConfigCorrupt(ILogger logger, Exception ex, string path);
}
