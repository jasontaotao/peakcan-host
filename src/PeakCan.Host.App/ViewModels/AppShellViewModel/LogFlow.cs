using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class AppShellViewModel
{
    // Flow D: Log helpers (v3.8.8 PATCH F1 + earlier).
    // Methods moved verbatim from AppShellViewModel.cs.
    //
    // All 9 helpers are [LoggerMessage] source-gen declarations.
    // The methods are deliberately not called from hot paths; their
    // only call site is the VM commands (Flow A + Flow C).

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Connect OK on handle 0x{Handle:X2}")]
    private static partial void LogConnectOk(ILogger logger, ushort handle);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connect failed on handle 0x{Handle:X2}: {Code} {Message}")]
    private static partial void LogConnectFailed(ILogger logger, ushort handle, ErrorCode code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Connect threw on handle 0x{Handle:X2}")]
    private static partial void LogConnectThrew(ILogger logger, ushort handle, Exception ex);

    // v3.8.8 PATCH F1: best-effort wrapper for the catch-arm
    // UnregisterChannel call. If the router itself throws (e.g. lock
    // contention or another sink's DisposeAsync propagating), we log
    // and continue so the channel dispose still runs.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Connect catch-arm UnregisterChannel threw on handle 0x{Handle:X2}")]
    private static partial void LogUnregisterFailed(ILogger logger, ushort handle, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disconnect OK on handle 0x{Handle:X2}")]
    private static partial void LogDisconnectOk(ILogger logger, ushort handle);

    [LoggerMessage(Level = LogLevel.Error, Message = "Disconnect threw on handle 0x{Handle:X2}")]
    private static partial void LogDisconnectThrew(ILogger logger, ushort handle, Exception ex);
}