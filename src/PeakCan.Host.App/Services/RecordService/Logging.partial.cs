using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services;

public sealed partial class RecordService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Recording started: {Path} ({Format})")]
    private static partial void LogRecordingStarted(ILogger logger, string path, string format);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording stopped: {Frames} frames written")]
    private static partial void LogRecordingStopped(ILogger logger, long frames);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording failed to start: {Path}")]
    private static partial void LogRecordingFailed(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording stop failed")]
    private static partial void LogRecordingStopFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Frame write failed (recording continues)")]
    private static partial void LogFrameWriteFailed(ILogger logger, Exception ex);

    // v1.2.12 PATCH Item 11: sink OnError → ILogger. The previous
    // Debug.WriteLine was stripped in Release builds (DEBUG not defined),
    // leaving production with no record of forwarded errors. Per service
    // EventId (6001) keeps the telemetry stream unambiguous.
    [LoggerMessage(EventId = 6001, Level = LogLevel.Warning, Message = "{Service} OnError forwarded")]
    private static partial void LogSinkError(ILogger logger, Exception ex, string service);
}