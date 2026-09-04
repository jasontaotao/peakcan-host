using Microsoft.Extensions.Logging;

namespace PeakCan.HIL.Core.J1939;

public sealed partial class J1939TpLayer
{
    // 注：brief 首选 ILogger? 可空参数，但 MEL source-gen 生成的实现体对可空 logger
    // 直接解引用（if (logger.IsEnabled(...))），每个方法在生成文件里产生一条 CS8602
    // 警告（实测：LoggerMessage.g.cs 330/342/.../414 共 8 条；本文件 8 个方法一一对应）。
    // 按 brief 注释预案（brief Step 6 末行）回退为非空 ILogger，
    // 调用点统一传 `_logger ?? NullLogger<J1939TpLayer>.Instance`（泛型 NullLogger，
    // 非泛型 NullLogger.Instance 与 ILogger<J1939TpLayer>? 做 ?? 不兼容 → CS0019，实测）。
    // 与 IsoTpLayer/LoggingFlow.cs 既有先例（非空 ILogger 参数）一致。

    [LoggerMessage(EventId = 3101, Level = LogLevel.Error, Message = "J1939TpLayer send failed for ID 0x{Id:X}")]
    internal static partial void LogSendFailed(ILogger logger, Exception ex, uint id);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Error, Message = "J1939TpLayer event handler threw for {Length}-byte payload")]
    private static partial void LogMessageHandlerFailed(ILogger logger, Exception ex, int length);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Warning, Message = "J1939TP declared length {Length} exceeds MaxPayloadBytes {Max}, dropping")]
    private static partial void LogDeclaredLengthExceeds(ILogger logger, int length, int max);

    [LoggerMessage(EventId = 3104, Level = LogLevel.Debug, Message = "J1939TP session superseded (SA {Sa:X2} DA {Da:X2} PGN 0x{Pgn:X4})")]
    private static partial void LogSessionSuperseded(ILogger logger, byte sa, byte da, uint pgn);

    [LoggerMessage(EventId = 3105, Level = LogLevel.Warning, Message = "J1939TP receive session T1 timeout (SA {Sa:X2} DA {Da:X2} PGN 0x{Pgn:X4})")]
    internal static partial void LogSessionTimeout(ILogger logger, byte sa, byte da, uint pgn);

    [LoggerMessage(EventId = 3106, Level = LogLevel.Warning, Message = "J1939TP session evicted (capacity {Max})")]
    private static partial void LogSessionEvicted(ILogger logger, int max);

    [LoggerMessage(EventId = 3107, Level = LogLevel.Warning, Message = "J1939TP DT sequence gap (expected {Expected}, got {Actual})")]
    private static partial void LogSequenceGap(ILogger logger, int expected, int actual);

    [LoggerMessage(EventId = 3108, Level = LogLevel.Debug, Message = "J1939TP TP.CM control {Control} received")]
    private static partial void LogControlReceived(ILogger logger, string control);
}
