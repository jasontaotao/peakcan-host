using Microsoft.Extensions.Logging;
using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// PEAK-SDK implementation of <see cref="IChannelEnumerator"/>. Probes
/// PCAN-USB channels 1–16 (handles 0x51–0x60) and returns those that
/// responded to <c>PCANBasic.Initialize</c>.
/// <para>
/// <b>Why 16?</b> PEAK supports up to 16 USB channels per driver
/// instance. The MVP only uses channel 1 (0x51), but enumerating all
/// 16 future-proofs the UI for multi-channel setups.
/// </para>
/// </summary>
public sealed partial class PeakChannelEnumerator : IChannelEnumerator
{
    private readonly ILogger<PeakChannelEnumerator> _logger;

    /// <summary>
    /// PEAK PCAN-USB channel handles. 0x51 = PCAN_USBBUS1, 0x52 =
    /// PCAN_USBBUS2, ..., 0x60 = PCAN_USBBUS16.
    /// </summary>
    private static readonly ushort[] UsbHandles =
        Enumerable.Range(0x51, 16).Select(h => (ushort)h).ToArray();

    public PeakChannelEnumerator(ILogger<PeakChannelEnumerator> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ChannelInfo> Enumerate()
    {
        var result = new List<ChannelInfo>();
        foreach (var handle in UsbHandles)
        {
            try
            {
                var status = PCANBasic.Initialize(handle, TPCANBaudrate.PCAN_BAUD_1M);
                if (PeakErrorMapper.IsOk((uint)status))
                {
                    result.Add(new ChannelInfo(handle, $"PCAN-USB {handle - 0x50}"));
                    // Uninitialize immediately so we don't hold the channel.
                    try { PCANBasic.Uninitialize(handle); }
                    catch { /* best-effort */ }
                }
            }
            catch (Exception ex)
            {
                // DllNotFound, EntryPointNotFound, etc. on machines
                // without the PEAK driver. Log once and stop probing.
                LogProbeFailed(_logger, handle, ex);
                break;
            }
        }
        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Channel probe failed on handle 0x{Handle:X2}, stopping enumeration")]
    private static partial void LogProbeFailed(ILogger logger, ushort handle, Exception error);
}
