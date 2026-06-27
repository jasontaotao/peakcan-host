using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Composition;

/// <summary>
/// v1.2.12 PATCH Item 2: log helper for the IsoTpLayer factory's async send
/// callback. Implemented as a separate non-partial class to avoid the WPF
/// _wpftmp.csproj / [LoggerMessage] source-gen collision that breaks the
/// build when declared on AppHostBuilder itself.
/// </summary>
internal static class IsoTpSendFailedLog
{
    public static void Log(ILogger logger, Exception ex, uint id)
    {
        // CA1848/CA2254 are silenced here because [LoggerMessage] source-gen
        // cannot be applied to a static method on a non-partial class. The
        // call site is one per failed send — not a hot path.
#pragma warning disable CA1848
#pragma warning disable CA2254
        logger.Log(
            LogLevel.Error,
            new EventId(3101, "IsoTpSendFailed"),
            $"IsoTpLayer send callback failed for ID 0x{id:X}",
            ex);
#pragma warning restore CA1848
#pragma warning restore CA2254
    }
}