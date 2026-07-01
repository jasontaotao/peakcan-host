using System.IO;

namespace PeakCan.Host.Core.Uds.IsoTp;

/// <summary>
/// v1.2.13 PATCH Item 5: thrown by <see cref="IsoTpLayer.SendCanFrameAsync"/>
/// when the SDK send callback fails. Distinguishes "send failure" from
/// routing/transport failures (which use other exception types). The
/// AppHostBuilder outer catch pattern-matches on this type to skip
/// the double-log that would otherwise duplicate the [LoggerMessage]
/// LogIsoTpSendFailed event (id 3001).
/// </summary>
public sealed class IsoTpSendFailedException : IOException
{
    /// <summary>CAN ID of the frame that failed to send.</summary>
    public uint CanId { get; }

    /// <summary>Position in the multi-frame burst (0 for FF / SF; 1..N for CF).</summary>
    public int FrameIndex { get; }

    public IsoTpSendFailedException(uint canId, int frameIndex, Exception innerException)
        : base($"IsoTp send failed at frame {frameIndex} for CAN ID 0x{canId:X}: {innerException.Message}", innerException)
    {
        CanId = canId;
        FrameIndex = frameIndex;
    }
}
