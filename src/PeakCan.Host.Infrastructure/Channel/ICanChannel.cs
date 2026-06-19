using PeakCan.Host.Core;
// PEAK SDK type referenced in the BaudRate record's ClassicCode field.
// TPCANBaudrate is the classic-CAN baud-rate enum (PCAN_BAUD_xxx).
using Peak.Can.Basic.BackwardCompatibility;

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
/// <para>
/// A channel is single-use: after <see cref="IAsyncDisposable.DisposeAsync"/>
/// the internal <c>CancellationTokenSource</c> is gone and any further
/// <see cref="ConnectAsync"/> call will throw <see cref="ObjectDisposedException"/>.
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
/// PEAK bitrate descriptor + human-readable label + CAN-FD flag +
/// (for classic rates only) the matching <see cref="TPCANBaudrate"/> enum value.
/// <para>
/// <see cref="Descriptor"/> is a PEAK-format string accepted by
/// <c>PCANBasic.Initialize</c> (classic, via <see cref="ClassicCode"/>) and
/// <c>PCANBasic.InitializeFD</c> (FD, the descriptor itself). The pre-canned
/// constants below assume a 20 MHz nominal clock (typical for PCAN-USB FD);
/// other clocks require a different descriptor — use
/// <see cref="FromDescriptor"/> to build a custom rate.
/// </para>
/// </summary>
public readonly record struct BaudRate(
    string Descriptor,
    string Name,
    bool IsFd,
    TPCANBaudrate? ClassicCode = null)
{
    // Classic CAN bitrate strings (20 MHz clock). Each preset pairs the
    // descriptor with the corresponding TPCANBaudrate enum value so
    // ConnectAsync can pick the right API without string-matching the descriptor.
    public static readonly BaudRate Can125kbps = new(
        "f_clock_mhz=20, nom_brp=8, nom_tseg1=8, nom_tseg2=3, nom_sjw=2",
        "125 kbps", false, TPCANBaudrate.PCAN_BAUD_125K);
    public static readonly BaudRate Can250kbps = new(
        "f_clock_mhz=20, nom_brp=4, nom_tseg1=8, nom_tseg2=3, nom_sjw=2",
        "250 kbps", false, TPCANBaudrate.PCAN_BAUD_250K);
    public static readonly BaudRate Can500kbps = new(
        "f_clock_mhz=20, nom_brp=2, nom_tseg1=8, nom_tseg2=3, nom_sjw=2",
        "500 kbps", false, TPCANBaudrate.PCAN_BAUD_500K);
    public static readonly BaudRate Can1Mbps = new(
        "f_clock_mhz=20, nom_brp=1, nom_tseg1=8, nom_tseg2=3, nom_sjw=2",
        "1 Mbps", false, TPCANBaudrate.PCAN_BAUD_1M);

    // CAN FD bitrate strings (20 MHz clock, nominal 1 Mbps, data phase varies).
    // ClassicCode is null because the classic Initialize API does not accept
    // these strings.
    public static readonly BaudRate CanFd1Mbps = new(
        "f_clock_mhz=20, nom_brp=1, nom_tseg1=8, nom_tseg2=3, nom_sjw=2, data_brp=1, data_tseg1=4, data_tseg2=3, data_sjw=2",
        "1 Mbps (FD)", true, null);
    public static readonly BaudRate CanFd2Mbps = new(
        "f_clock_mhz=20, nom_brp=1, nom_tseg1=8, nom_tseg2=3, nom_sjw=2, data_brp=1, data_tseg1=2, data_tseg2=2, data_sjw=1",
        "2 Mbps (FD)", true, null);
    public static readonly BaudRate CanFd5Mbps = new(
        "f_clock_mhz=20, nom_brp=1, nom_tseg1=8, nom_tseg2=3, nom_sjw=2, data_brp=1, data_tseg1=1, data_tseg2=1, data_sjw=1",
        "5 Mbps (FD)", true, null);

    /// <summary>
    /// Build a custom rate from a PEAK bitrate descriptor. <paramref name="isFd"/>
    /// selects which API the descriptor is passed to; the classic API additionally
    /// requires a <paramref name="classicCode"/> matching one of the
    /// <c>PCAN_BAUD_*</c> enum values.
    /// </summary>
    public static BaudRate FromDescriptor(string descriptor, string name, bool isFd, TPCANBaudrate? classicCode = null)
        => new(descriptor, name, isFd, classicCode);
}
