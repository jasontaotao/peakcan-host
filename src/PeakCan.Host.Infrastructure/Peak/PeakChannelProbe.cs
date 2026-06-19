using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// PEAK-SDK implementation of <see cref="IChannelProbe"/>. Calls
/// <c>PCANBasic.Initialize</c> with a fixed 1 Mbps baud rate to
/// detect whether a device is attached to the given handle, then
/// uninitializes immediately so the channel is left clean for the
/// real <see cref="PeakCanChannel.ConnectAsync"/>.
/// <para>
/// <b>Baud rate choice:</b> 1 Mbps is the standard PEAK reference
/// rate; the probe is a binary presence check, not a link-quality
/// measurement. The actual Connect uses the user-configured
/// <see cref="BaudRate"/> (default 1 Mbps CAN FD).
/// </para>
/// <para>
/// <b>Why a separate service?</b> extracting the SDK call out of the
/// VM lets the NetArchTest rule <c>App_Should_Not_Depend_On_Peak_Can_Basic</c>
/// pass and keeps the VM testable without the PEAK native libraries
/// (we can inject a fake in xunit).
/// </para>
/// </summary>
public sealed class PeakChannelProbe : IChannelProbe
{
    /// <summary>
    /// Probe the channel at <paramref name="handle"/> via
    /// <c>PCANBasic.Initialize</c> at 1 Mbps. On success, immediately
    /// <c>Uninitialize</c> so the channel is not held open. Failures
    /// (no driver, no hardware) are mapped through
    /// <see cref="PeakErrorMapper"/> to a human-readable message.
    /// </summary>
    public ProbeResult Probe(ushort handle)
    {
        try
        {
            var status = PCANBasic.Initialize(handle, TPCANBaudrate.PCAN_BAUD_1M);
            if (PeakErrorMapper.IsOk((uint)status))
            {
                // Best-effort uninit so we don't leave the channel held
                // open between probe and connect. The uninit call is
                // expected to succeed; any failure is swallowed (the
                // probe has already succeeded; the channel is just
                // kept allocated until the next process restart).
                try { PCANBasic.Uninitialize(handle); }
                catch { /* swallow — probe already succeeded */ }
                return new ProbeResult(true, $"PEAK channel 0x{handle:X2} detected");
            }
            var (code, msg) = PeakErrorMapper.ToErrorCode((uint)status);
            return new ProbeResult(false, $"Probe failed: {code} {msg}");
        }
        catch (Exception ex)
        {
            // DllNotFound, EntryPointNotFound, etc. on machines
            // without the PEAK driver installed.
            return new ProbeResult(false, $"Probe exception: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
