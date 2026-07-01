namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// PCAN-Basic status code constants (subset of <c>TPCANStatus</c>). We only
/// expose the codes we actually surface to the UI; the full set lives in
/// the PEAK SDK header. Values match <c>PCAN_ERROR_xxx</c> from
/// <c>Peak.PCANBasic.NET</c>.
/// </summary>
public static class PeakError
{
    /// <summary>No error.</summary>
    public const uint OK = 0x00000000;

    /// <summary>Transmit buffer full (hardware TX FIFO overflow).</summary>
    public const uint XMTFULL = 0x00000001;

    /// <summary>Receive overrun (hardware RX FIFO overflow).</summary>
    public const uint OVERRUN = 0x00000002;

    /// <summary>Bus error: light (warning) form (stuff/bit/format/CRC).</summary>
    public const uint BUSLIGHT = 0x00000004;

    /// <summary>Bus error: heavy form (repeated errors, may be heading to bus-off).</summary>
    public const uint BUSHEAVY = 0x00000008;

    /// <summary>PCAN-USB device handle invalid or not initialized.</summary>
    public const uint ILLHW = 0x00000009;

    /// <summary>Driver failed self-test on load.</summary>
    public const uint REGTEST = 0x0000000A;

    /// <summary>One of the parameters passed to a PCAN-Basic call was invalid.</summary>
    public const uint PARAM = 0x0000000B;

    /// <summary>No driver loaded (PCAN driver not installed or not started).</summary>
    public const uint NODRIVER = 0x00000020;

    /// <summary>Bus-off state — hardware has stopped participating on the bus.</summary>
    public const uint BUSOFF = 0x00000040;
}
