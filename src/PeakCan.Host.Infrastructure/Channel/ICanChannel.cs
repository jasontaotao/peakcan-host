using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Channel;

/// <summary>
/// One CAN channel (one PCAN-USB handle). Owns the connect/disconnect lifecycle
/// and exposes an asynchronous read path via <see cref="FrameReceived"/>.
/// <para>
/// The <see cref="FrameReceived"/> event is raised on a background read loop
/// (the SDK's <c>PCANBasic.Read</c> polling thread or its event-driven
/// <c>SetRcvEvent</c> equivalent) — consumers must marshal back to the UI
/// thread themselves. Subscribers should not throw; the
/// <see cref="ChannelRouter"/> isolates per-sink exceptions.
/// </para>
/// </summary>
public interface ICanChannel : IAsyncDisposable
{
    /// <summary>Stable channel handle (PCAN_USBBUS1 etc.).</summary>
    ChannelId Id { get; }

    /// <summary>True after a successful <see cref="ConnectAsync"/> until <see cref="DisconnectAsync"/>.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Initialize the channel at <paramref name="baud"/>. Sets
    /// <see cref="IsConnected"/> to true on success; on failure returns a
    /// <see cref="Result{T}"/> with the mapped PEAK error code.
    /// </summary>
    Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default);

    /// <summary>Stop the read loop and call <c>PCANBasic.Uninitialize</c>. Idempotent.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Transmit one <paramref name="frame"/>. Returns a failed <see cref="Result{T}"/>
    /// when not connected or when PEAK reports an error.
    /// </summary>
    ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default);

    /// <summary>
    /// Fired by the read loop for every received frame. Subscribers must be
    /// thread-safe and must not block (the read loop continues immediately).
    /// </summary>
    event Action<CanFrame>? FrameReceived;
}

/// <summary>
/// PEAK bitrate descriptor + human-readable label + CAN-FD flag.
/// <para>
/// <see cref="Descriptor"/> is a PEAK-format string accepted by
/// <c>PCANBasic.Initialize</c> (classic) and <c>PCANBasic.InitializeFD</c>
/// (FD). The pre-canned constants below assume a 20 MHz nominal clock
/// (typical for PCAN-USB FD); other clocks require a different descriptor.
/// </para>
/// </summary>
public readonly record struct BaudRate(string Descriptor, string Name, bool IsFd)
{
    // Classic CAN bitrate strings (20 MHz clock).
    public static readonly BaudRate Can125kbps =
        new("f_clock_mhz=20, nom_brp=8, nom_tseg1=8, nom_tseg2=3, nom_sjw=2", "125 kbps", false);
    public static readonly BaudRate Can250kbps =
        new("f_clock_mhz=20, nom_brp=4, nom_tseg1=8, nom_tseg2=3, nom_sjw=2", "250 kbps", false);
    public static readonly BaudRate Can500kbps =
        new("f_clock_mhz=20, nom_brp=2, nom_tseg1=8, nom_tseg2=3, nom_sjw=2", "500 kbps", false);
    public static readonly BaudRate Can1Mbps =
        new("f_clock_mhz=20, nom_brp=1, nom_tseg1=8, nom_tseg2=3, nom_sjw=2", "1 Mbps", false);

    // CAN FD bitrate strings (20 MHz clock, nominal 1 Mbps, data phase varies).
    public static readonly BaudRate CanFd1Mbps =
        new("f_clock_mhz=20, nom_brp=1, nom_tseg1=8, nom_tseg2=3, nom_sjw=2, data_brp=1, data_tseg1=4, data_tseg2=3, data_sjw=2",
            "1 Mbps (FD)", true);
    public static readonly BaudRate CanFd2Mbps =
        new("f_clock_mhz=20, nom_brp=1, nom_tseg1=8, nom_tseg2=3, nom_sjw=2, data_brp=1, data_tseg1=2, data_tseg2=2, data_sjw=1",
            "2 Mbps (FD)", true);
    public static readonly BaudRate CanFd5Mbps =
        new("f_clock_mhz=20, nom_brp=1, nom_tseg1=8, nom_tseg2=3, nom_sjw=2, data_brp=1, data_tseg1=1, data_tseg2=1, data_sjw=1",
            "5 Mbps (FD)", true);
}

/// <summary>
/// Sentinel "no value" return for operations whose success carries no payload
/// (e.g. <c>ConnectAsync</c>). Use this in place of <c>Result&lt;object&gt;</c>
/// to keep <see cref="Result{T}"/> non-nullable.
/// </summary>
public readonly record struct Unit;
