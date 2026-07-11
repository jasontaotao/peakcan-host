using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Uds.IsoTp;

public sealed partial class IsoTpLayer
{
    // Flow G: Logging (v1.2.12 PATCH Items 2/3/8).
    // 3 [LoggerMessage] partial methods moved verbatim from IsoTpLayer.cs.
    //
    // Source-gen: CommunityToolkit.Mvvm-style [LoggerMessage] source generator
    // processes each partial independently. The generated body goes in the
    // partial containing the declaration. The public/internal visibility
    // modifiers travel with the method.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - LogIsoTpSendFailed (event id 3001) <- SendCanFrameAsync catch (Flow B)
    //   - LogIsoTpHandlerFailed (event id 3002) <- HandleConsecutiveFrame catch (Flow D)
    //   - LogIsoTpFfLengthTooLarge (event id 3003) <- HandleFirstFrame (Flow D)

    // v1.2.12 PATCH Item 2: log send-callback exceptions at Error. The upper
    // layers (UdsClient's P2* timeout, BS-gate timeout) provide back-pressure,
    // so a single failed send is logged and the transport continues.
    //
    // `internal` (not `private`) so the App factory can call this directly
    // instead of maintaining a duplicate log helper (single source of truth
    // for the event id). Core grants InternalsVisibleTo("PeakCan.Host.App")
    // in AssemblyInfo.cs.
    [LoggerMessage(EventId = 3001, Level = LogLevel.Error, Message = "IsoTpLayer send failed for ID 0x{Id:X}")]
    internal static partial void LogIsoTpSendFailed(ILogger logger, Exception ex, uint id);

    // v1.2.12 PATCH Item 3: log MessageReceived subscriber exceptions at Error.
    // The receive handler is invoked outside the lock so the layer's
    // reassembly state remains intact; a throwing subscriber must NOT
    // propagate onto the SDK read thread nor poison subsequent frames.
    // Single source of truth for the "handler threw" event (id 3002).
    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "IsoTpLayer MessageReceived handler threw for {Length}-byte message")]
    private static partial void LogIsoTpHandlerFailed(ILogger logger, Exception ex, int length);

    // v1.2.12 PATCH Item 8: log rejected FirstFrame at Warning. The frame
    // is dropped (not propagated) so the SDK read thread stays unblocked,
    // but operators need visibility into an ECU that's streaming malformed
    // FFs (likely a fuzz harness or a misconfigured sender).
    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "IsoTp FirstFrame length {Length} exceeds MaxMessageLength {Max}, dropping")]
    private static partial void LogIsoTpFfLengthTooLarge(ILogger logger, int length, int max);
}