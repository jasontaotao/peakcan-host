namespace PeakCan.Host.Core;

/// <summary>
/// One CAN channel (one PCAN-USB handle). Owns the connect/disconnect lifecycle
/// and exposes an asynchronous read path via <see cref="FrameReceived"/>.
/// <para>
/// The <see cref="FrameReceived"/> event is raised on a background read loop
/// (the SDK's <c>PCANBasic.Read</c> polling thread or its event-driven
/// <c>SetRcvEvent</c> equivalent) — consumers must marshal back to the UI
/// thread themselves. Subscribers should not throw; the
/// <c>ChannelRouter</c> isolates per-sink exceptions.
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
/// PEAK bitrate descriptor + human-readable label + CAN-FD flag.
/// <para>
/// <see cref="Descriptor"/> is a PEAK-format string accepted by
/// <c>PCANBasic.InitializeFD</c>; the classic <c>PCANBasic.Initialize</c>
/// API requires matching the <see cref="Name"/> to one of the four
/// <c>Can*kbps</c> presets, which the PEAK adapter maps internally to the
/// corresponding <c>PCAN_BAUD_*</c> enum value. The pre-canned constants
/// below assume a 20 MHz nominal clock (typical for PCAN-USB FD);
/// other clocks require a different descriptor — use
/// <see cref="FromDescriptor"/> to build a custom rate for FD mode.
/// </para>
/// </summary>
public readonly record struct BaudRate(
    string Descriptor,
    string Name,
    bool IsFd)
{
    // Classic CAN bitrate strings (20 MHz clock). The PEAK adapter
    // recognizes these four names and dispatches to PCANBasic.Initialize
    // with the matching PCAN_BAUD_* enum.
    public static readonly BaudRate Can125kbps = new(
        "f_clock_mhz=20, nom_brp=8, nom_tseg1=8, nom_tseg2=3, nom_sjw=2",
        "125 kbps", false);
    public static readonly BaudRate Can250kbps = new(
        "f_clock_mhz=20, nom_brp=4, nom_tseg1=8, nom_tseg2=3, nom_sjw=2",
        "250 kbps", false);
    public static readonly BaudRate Can500kbps = new(
        "f_clock_mhz=20, nom_brp=2, nom_tseg1=8, nom_tseg2=3, nom_sjw=2",
        "500 kbps", false);
    public static readonly BaudRate Can1Mbps = new(
        "f_clock_mhz=20, nom_brp=1, nom_tseg1=8, nom_tseg2=3, nom_sjw=2",
        "1 Mbps", false);

    // CAN FD bitrate strings (20 MHz clock, nominal 1 Mbps, data phase varies).
    public static readonly BaudRate CanFd1Mbps = new(
        "f_clock_mhz=20, nom_brp=1, nom_tseg1=8, nom_tseg2=3, nom_sjw=2, data_brp=1, data_tseg1=4, data_tseg2=3, data_sjw=2",
        "1 Mbps (FD)", true);
    public static readonly BaudRate CanFd2Mbps = new(
        "f_clock_mhz=20, nom_brp=1, nom_tseg1=8, nom_tseg2=3, nom_sjw=2, data_brp=1, data_tseg1=2, data_tseg2=2, data_sjw=1",
        "2 Mbps (FD)", true);
    public static readonly BaudRate CanFd5Mbps = new(
        "f_clock_mhz=20, nom_brp=1, nom_tseg1=8, nom_tseg2=3, nom_sjw=2, data_brp=1, data_tseg1=1, data_tseg2=1, data_sjw=1",
        "5 Mbps (FD)", true);

    /// <summary>
    /// Build a custom FD rate from a PEAK bitrate descriptor. Classic
    /// (non-FD) custom rates are not supported here — the PEAK classic
    /// API requires the matching <c>PCAN_BAUD_*</c> enum value, which
    /// is not representable in Core. Use one of the four
    /// <c>Can*kbps</c> presets for classic custom rates.
    /// <para>
    /// This overload always produces an FD <see cref="BaudRate"/> (the
    /// <c>isFd</c> parameter was dropped when <see cref="BaudRate"/>
    /// stopped carrying the PEAK <c>TPCANBaudrate?</c> classic-code
    /// field, to keep Core free of any PEAK SDK dependency per
    /// NetArchTest rule 2). If a future change reintroduces a
    /// Core-safe PEAK code mapping, this method should regain the
    /// <c>classicCode</c> parameter rather than guessing <c>isFd</c>.
    /// </para>
    /// </summary>
    // TODO: reintroduce the (string, string, TPCANBaudrate?) overload when
    // a Core-safe PEAK classic-code mapping exists. Today the only
    // callers are inside the four Can*kbps / CanFd*Mbps presets above,
    // so the constraint is invisible to consumers — but a future
    // third-party extension wanting a custom classic rate will hit
    // this and need a clear path. The Obsolete attribute on this
    // signature is the only compile-time signal we can offer until
    // then; remove it together with this TODO when the new overload
    // ships.
    [Obsolete("Custom classic-CAN rates are not representable in Core (no PEAK SDK dependency). Use the four Can*kbps presets, or the CanFd*Mbps presets for FD. If a Core-safe PEAK code mapping is added later, this method will be replaced by a (descriptor, name, classicCode) overload.")]
    public static BaudRate FromDescriptor(string descriptor, string name)
        => new(descriptor, name, true);
}
