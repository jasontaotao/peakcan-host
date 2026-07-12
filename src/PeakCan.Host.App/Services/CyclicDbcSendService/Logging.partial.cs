using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services;

public sealed partial class CyclicDbcSendService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Cyclic DBC send started every {Interval}ms")]
    private static partial void LogCyclicDbcStarted(ILogger logger, double interval);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cyclic DBC send stopped: {Count} frames sent")]
    private static partial void LogCyclicDbcStopped(ILogger logger, long count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cyclic DBC send message changed mid-run (was {OldId}, now {NewId}); auto-stopped")]
    private static partial void LogCyclicDbcMessageChanged(ILogger logger, uint? oldId, uint newId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cyclic DBC send failed for {Id}: {Code} {Message}")]
    private static partial void LogCyclicDbcSendFailed(ILogger logger, CanId id, ErrorCode code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cyclic DBC send threw for {Id}")]
    private static partial void LogCyclicDbcSendThrew(ILogger logger, CanId id, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cyclic DBC encode threw for message {MessageId}")]
    private static partial void LogCyclicDbcEncodeThrew(ILogger logger, uint messageId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cyclic DBC frame provider threw")]
    private static partial void LogCyclicDbcProviderThrew(ILogger logger, Exception ex);
}