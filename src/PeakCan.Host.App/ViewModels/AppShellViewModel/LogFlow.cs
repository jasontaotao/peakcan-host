using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class AppShellViewModel
{
    // Flow D: Log helpers (v3.8.8 PATCH F1 + earlier).
    // Methods moved verbatim from AppShellViewModel.cs.
    //
    // All 3 remaining helpers are [LoggerMessage] source-gen declarations.
    // The methods are deliberately not called from hot paths; their
    // only call site is the VM commands (Flow A + Flow C).
    // P2-1 真拆类（2026-09-06）：connect/disconnect 六个 Log* 随连接循环
    // 移交 ChannelConnectionCoordinator（同类内重声明，同文本）；本类仅保留
    // 探测/枚举路径的日志。

    // LoggerMessage source-generated helpers silence CA1848 (use LoggerMessage
    // source generators) and CA1873 (avoid expensive arg computation in
    // disabled loggers). The methods are deliberately not called from hot
    // paths; their only call site is the VM commands.

    [LoggerMessage(Level = LogLevel.Information, Message = "Open DBC menu invoked")]
    private static partial void LogOpenDbcInvoked(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Probe OK on handle 0x{Handle:X2}")]
    private static partial void LogProbeOk(ILogger logger, ushort handle);

    [LoggerMessage(Level = LogLevel.Error, Message = "Probe threw on handle 0x{Handle:X2}")]
    private static partial void LogProbeThrew(ILogger logger, ushort handle, Exception ex);
}