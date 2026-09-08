using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Channel.SecOc;

/// <summary>
/// Single assembly point for the HIL channel decorator chain (spec §5-D1):
/// <c>SecOcChannel( ReceivePathFaultInjector( FaultInjector( raw ) ) )</c>.
/// One static order satisfies both directions: TX signs before inner corruption,
/// RX verifies after inner corruption. Idempotent — decorators are added only
/// when not already present on the chain.
/// </summary>
public static class HilChannelComposer
{
    public static ICanChannel Compose(
        ICanChannel raw,
        bool enableFaultInjection = false,
        IReadOnlyDictionary<uint, SecOcPduConfig>? secocPdus = null,
        SecOcVerdictTable? verdictTable = null,
        SecOcStats? stats = null,
        ILogger? logger = null)
    {
        var channel = raw;

        if (enableFaultInjection && channel is not FaultInjector)
            channel = new FaultInjector(channel);
        if (enableFaultInjection && channel is not ReceivePathFaultInjector)
            channel = new ReceivePathFaultInjector(channel);

        if (secocPdus is { Count: > 0 })
            channel = new SecOcChannel(channel,
                new SecOcChannelOptions { ProtectedPdus = secocPdus },
                verdictTable, stats, logger);

        return channel;
    }
}
